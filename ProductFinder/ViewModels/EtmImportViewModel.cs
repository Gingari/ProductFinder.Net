using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ProductFinder.Infrastructure;
using ProductFinder.Models;
using ProductFinder.Services;

namespace ProductFinder.ViewModels;

public sealed class EtmImportViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IEtmBrowserService _browser;
    private readonly CatalogWriteService _writer;
    private readonly Func<EtmProduct, IReadOnlyList<CatalogEntry>, EtmDuplicateDecision?> _chooseDuplicate;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private string _query;
    private string _status = "Проверьте название из заявки, при необходимости измените его и нажмите «Найти на ЭТМ».";
    private bool _isBusy;
    private bool _isSaving;
    private bool _disposed;
    private bool _useBackground = true;
    private EtmCandidate? _selected;
    private EtmProduct? _parsed;
    public EtmImportViewModel(string sourceText, IEtmBrowserService browser, CatalogWriteService writer,
        Func<EtmProduct, IReadOnlyList<CatalogEntry>, EtmDuplicateDecision?> chooseDuplicate)
    {
        SourceText = sourceText;
        _query = sourceText;
        _browser = browser;
        _writer = writer;
        _chooseDuplicate = chooseDuplicate;
        SearchCommand = new(SearchAsync, () => CanEdit && Query.Trim().Length >= 2, SetError);
        ParseCommand = new(ParseAsync, () => CanEdit && Selected is not null, SetError);
        SaveCommand = new(SaveAsync, () => CanEdit && ParsedProduct is not null, SetError);
        ShowBrowserCommand = new(() => RunAsync(async token =>
        {
            UseBackground = false;
            await ConfigureBrowserAsync(token);
            await _browser.ShowBrowserAsync(token);
            Status = "Браузер открыт. Выполните проверку сайта, затем повторите поиск в приложении.";
        }), () => CanEdit, SetError);
        CancelCommand = new(Cancel, () => IsBusy);
    }
    public bool UseBackground { get => _useBackground; set => SetProperty(ref _useBackground, value); }
    private Task ConfigureBrowserAsync(CancellationToken token) => _browser is IEtmBrowserModeService mode
        ? mode.SetBackgroundAsync(UseBackground, token) : Task.CompletedTask;
    public string SourceText { get; }
    public string Query
    {
        get => _query;
        set
        {
            if (!SetProperty(ref _query, value)) return;
            Results.Clear(); Selected = null; ParsedProduct = null;
            Status = "Запрос изменён. Нажмите «Найти на ЭТМ».";
            NotifyCommands();
        }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) { OnPropertyChanged(nameof(CanEdit)); NotifyCommands(); } }
    }
    public bool IsSaving { get => _isSaving; private set => SetProperty(ref _isSaving, value); }
    public bool CanEdit => !_disposed && !IsBusy;
    public EtmCandidate? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) { ParsedProduct = null; NotifyCommands(); } }
    }
    public EtmProduct? ParsedProduct
    {
        get => _parsed;
        private set { if (SetProperty(ref _parsed, value)) { OnPropertyChanged(nameof(HasParsedProduct)); NotifyCommands(); } }
    }
    public bool HasParsedProduct => ParsedProduct is not null;
    public RangeObservableCollection<EtmCandidate> Results { get; } = new();
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand ParseCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand ShowBrowserCommand { get; }
    public RelayCommand CancelCommand { get; }
    public EtmSaveResult? Result { get; private set; }
    public event EventHandler? Saved;
    private void NotifyCommands()
    {
        SearchCommand?.NotifyCanExecuteChanged(); ParseCommand?.NotifyCanExecuteChanged(); SaveCommand?.NotifyCanExecuteChanged();
        ShowBrowserCommand?.NotifyCanExecuteChanged(); CancelCommand?.NotifyCanExecuteChanged();
    }
    private void SetError(Exception ex) => Status = "Ошибка: " + ex.Message;
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (!CanEdit) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = cts;
        IsBusy = true;
        try { await action(cts.Token); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { if (!_disposed) Status = "Операция отменена. База не изменялась, если сохранение ещё не завершилось."; }
        catch (Exception ex) { if (!_disposed) SetError(ex); }
        finally { _operation = null; IsBusy = false; }
    }
    public Task SearchAsync() => RunAsync(async token =>
    {
        await ConfigureBrowserAsync(token);
        var query = Query.Trim();
        Results.Clear(); Selected = null; ParsedProduct = null;
        var progress = new Progress<string>(s => { if (!_disposed && !token.IsCancellationRequested) Status = s; });
        var results = await _browser.SearchAsync(query, progress, token);
        token.ThrowIfCancellationRequested();
        Results.AddRange(results);
        Status = results.Count == 0 ? "ЭТМ не показал подходящих результатов. Измените запрос и повторите поиск." : $"Найдено вариантов: {results.Count}. Выберите товар и нажмите «Отпарсить».";
    });
    public Task ParseAsync() => RunAsync(async token =>
    {
        var selected = Selected;
        if (selected is null) return;
        await ConfigureBrowserAsync(token);
        var progress = new Progress<string>(s => { if (!_disposed && !token.IsCancellationRequested) Status = s; });
        var product = await _browser.ReadProductAsync(selected, progress, token);
        token.ThrowIfCancellationRequested();
        ParsedProduct = product;
        Status = "Карточка прочитана. Проверьте название, артикул и производителя; запись в базу выполняется только кнопкой «Сохранить и добавить».";
    });
    public Task SaveAsync() => RunAsync(async token =>
    {
        var product = ParsedProduct;
        if (product is null) return;
        IsSaving = true;
        try
        {
            Status = "Проверка артикула в рабочей базе…";
            var existing = await _writer.FindAsync(product.Article, token);
            token.ThrowIfCancellationRequested();
            var decision = existing.Count > 0 ? _chooseDuplicate(product, existing) : null;
            if (existing.Count > 0 && decision is null) { Status = "Сохранение отменено. База не изменена."; return; }
            token.ThrowIfCancellationRequested();
            Status = "Сохранение в SQLite и создание резервной копии…";
            Result = await _writer.SaveAsync(product, existing, decision, token);
            Status = Result.Added ? "Товар добавлен в каталог." : Result.Replaced ? "Выбранная запись каталога обновлена." : "Использована существующая запись. База не изменена.";
            Saved?.Invoke(this, EventArgs.Empty);
        }
        finally { IsSaving = false; }
    });
    public void Cancel() => _operation?.Cancel();
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel();
        await _browser.DisposeAsync();
        _lifetime.Dispose();
    }
}
