using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using ProductFinder.Infrastructure;
using ProductFinder.Models;
using ProductFinder.Services;

namespace ProductFinder.ViewModels;

public sealed class ExcelImportViewModel : ObservableObject, IDisposable
{
    private readonly string _path;
    private readonly IExcelService _excel;
    private readonly Action<string> _showError;
    private readonly CancellationTokenSource _lifetime = new();
    private ExcelSheetData? _sheet;
    private bool _disposed;
    private bool _isBusy;
    private bool _suppressRebuild;
    private bool _suppressCount;
    private string _selectedSheet;
    private string _headerRowText = "1";
    private string _filter = "";
    private string _status = "";
    private int _articleColumn;
    private int _nameColumn = -1;
    private int _quantityColumn;
    private int _selectedCount;

    public ExcelImportViewModel(string path, IReadOnlyList<string> sheets, IExcelService excel, Action<string> showError)
    {
        _path = path;
        _excel = excel;
        _showError = showError;
        Sheets = sheets;
        _selectedSheet = sheets.FirstOrDefault() ?? "";
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = item => item is ExcelImportRow row && MatchesFilter(row);
        ApplyHeaderCommand = new(ApplyHeaderAsync, () => CanConfigure && _sheet is not null, ShowError);
        SelectAllCommand = new(() => SetChecks(true), () => CanConfigure);
        DeselectAllCommand = new(() => SetChecks(false), () => CanConfigure);
        InvertChecksCommand = new(() => SetChecks(null), () => CanConfigure);
    }
    public IReadOnlyList<string> Sheets { get; }
    public ObservableCollection<ExcelColumn> Columns { get; } = new();
    public ObservableCollection<ExcelColumn> NameColumns { get; } = new();
    public RangeObservableCollection<ExcelImportRow> Rows { get; } = new();
    public ICollectionView RowsView { get; }
    public AsyncCommand ApplyHeaderCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand DeselectAllCommand { get; }
    public RelayCommand InvertChecksCommand { get; }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanConfigure));
            OnPropertyChanged(nameof(CanAccept));
            ApplyHeaderCommand.NotifyCanExecuteChanged();
            SelectAllCommand.NotifyCanExecuteChanged();
            DeselectAllCommand.NotifyCanExecuteChanged();
            InvertChecksCommand.NotifyCanExecuteChanged();
        }
    }
    public bool CanConfigure => !_disposed && !IsBusy;
    public bool CanAccept => CanConfigure && _selectedCount > 0;
    public string SelectionSummary => $"Всего строк: {Rows.Count} · Выбрано: {_selectedCount}";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string HeaderRowText { get => _headerRowText; set => SetProperty(ref _headerRowText, value); }
    public string SelectedSheet
    {
        get => _selectedSheet;
        set { if (SetProperty(ref _selectedSheet, value ?? "") && !_disposed && !IsBusy) _ = LoadSheetAsync(); }
    }
    public int ArticleColumn
    {
        get => _articleColumn;
        set { if (SetProperty(ref _articleColumn, value)) QueueRebuild(); }
    }
    public int NameColumn
    {
        get => _nameColumn;
        set { if (SetProperty(ref _nameColumn, value)) QueueRebuild(); }
    }
    public int QuantityColumn
    {
        get => _quantityColumn;
        set { if (SetProperty(ref _quantityColumn, value)) QueueRebuild(); }
    }
    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value)) return;
            CommitViewEdit();
            RowsView.Refresh();
        }
    }
    public Task InitializeAsync() => LoadSheetAsync();
    private async Task LoadSheetAsync()
    {
        if (_disposed || IsBusy || SelectedSheet.Length == 0) return;
        IsBusy = true;
        Status = "Чтение листа…";
        try
        {
            var sheet = await _excel.ReadSheetAsync(_path, SelectedSheet, _lifetime.Token);
            if (_disposed) return;
            _sheet = sheet;
            HeaderRowText = ExcelRowExtractor.DetectHeader(sheet).ToString(CultureInfo.InvariantCulture);
            ConfigureColumns();
            await BuildPreviewAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { if (!_disposed) { ClearRows(); Status = "Ошибка чтения листа"; ShowError(ex); } }
        finally { if (!_disposed) IsBusy = false; }
    }
    private int HeaderRow()
    {
        if (!int.TryParse(HeaderRowText.Trim(), out var row) || row < 1 || row > Math.Max(1, Math.Min(100, _sheet?.Rows.Count ?? 0)))
            throw new InvalidOperationException("Укажите существующую строку заголовка от 1 до 100.");
        return row;
    }
    private void ConfigureColumns()
    {
        if (_sheet is null) return;
        var header = HeaderRow();
        _suppressRebuild = true;
        try
        {
            Columns.Clear();
            NameColumns.Clear();
            NameColumns.Add(new(-1, "— без имени —"));
            foreach (var column in ExcelRowExtractor.Columns(_sheet, header)) { Columns.Add(column); NameColumns.Add(column); }
            var mapping = ExcelRowExtractor.DetectMapping(_sheet, header);
            ArticleColumn = mapping.ArticleColumn;
            NameColumn = mapping.NameColumn;
            QuantityColumn = mapping.QuantityColumn;
            OnPropertyChanged(nameof(ArticleColumn));
            OnPropertyChanged(nameof(NameColumn));
            OnPropertyChanged(nameof(QuantityColumn));
        }
        finally { _suppressRebuild = false; }
    }
    private async Task ApplyHeaderAsync()
    {
        if (!CanConfigure || _sheet is null) return;
        IsBusy = true;
        try { ConfigureColumns(); await BuildPreviewAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { ShowError(ex); }
        finally { if (!_disposed) IsBusy = false; }
    }
    private void QueueRebuild()
    {
        if (!_disposed && !_suppressRebuild && _sheet is not null && !IsBusy) _ = RebuildAsync();
    }
    private async Task RebuildAsync()
    {
        IsBusy = true;
        try { await BuildPreviewAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { ShowError(ex); }
        finally { if (!_disposed) IsBusy = false; }
    }
    private async Task BuildPreviewAsync(CancellationToken token)
    {
        if (_sheet is null) return;
        var sheet = _sheet;
        var mapping = new ExcelColumnMapping(HeaderRow(), ArticleColumn, NameColumn, QuantityColumn);
        Status = "Подготовка строк…";
        var rows = await Task.Run(() => ExcelRowExtractor.Extract(sheet, mapping, token), token);
        if (_disposed) return;
        CommitViewEdit();
        foreach (var row in Rows) row.PropertyChanged -= OnRowChanged;
        using (RowsView.DeferRefresh()) Rows.ReplaceAll(rows);
        foreach (var row in Rows) row.PropertyChanged += OnRowChanged;
        _selectedCount = Rows.Count(r => r.IsSelected);
        RefreshCounts();
        Status = rows.Count == 0 ? "Нет строк после заголовка" : "Можно исправить артикул, название и количество перед импортом";
    }
    private void ClearRows()
    {
        CommitViewEdit();
        foreach (var row in Rows) row.PropertyChanged -= OnRowChanged;
        Rows.ReplaceAll(Array.Empty<ExcelImportRow>());
        _selectedCount = 0;
        RefreshCounts();
    }
    private bool MatchesFilter(ExcelImportRow row)
    {
        var filter = Filter.Trim();
        return filter.Length == 0 || row.Article.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || row.SourceText.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
    private void CommitViewEdit()
    {
        if (RowsView is IEditableCollectionView { IsEditingItem: true } editable) editable.CommitEdit();
    }
    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressCount || e.PropertyName != nameof(ExcelImportRow.IsSelected) || sender is not ExcelImportRow row) return;
        _selectedCount += row.IsSelected ? 1 : -1;
        RefreshCounts();
    }
    private void SetChecks(bool? state)
    {
        CommitViewEdit();
        _suppressCount = true;
        try { foreach (var row in RowsView.OfType<ExcelImportRow>().ToArray()) row.IsSelected = state ?? !row.IsSelected; }
        finally { _suppressCount = false; }
        _selectedCount = Rows.Count(r => r.IsSelected);
        RefreshCounts();
    }
    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanAccept));
    }
    public IReadOnlyList<RequestLine> GetSelectedLines() => ExcelRowExtractor.SelectedLines(Rows);
    private void ShowError(Exception ex) { if (!_disposed) _showError(ex.Message); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        foreach (var row in Rows) row.PropertyChanged -= OnRowChanged;
    }
}
