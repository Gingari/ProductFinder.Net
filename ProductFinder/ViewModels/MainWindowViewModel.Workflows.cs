using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProductFinder.Infrastructure;
using ProductFinder.Models;
using ProductFinder.Services;

namespace ProductFinder.ViewModels;

public sealed partial class MainWindowViewModel
{
    private IExcelService _excel = null!;
    private RequestImportService _requests = null!;
    private CancellationTokenSource? _operationCts;
    private bool _isProcessing;
    private int? _operationProgress;
    private string _operationStatus = "";

    public AsyncCommand ImportExcelCommand { get; private set; } = null!;
    public AsyncCommand ExportExcelCommand { get; private set; } = null!;
    public AsyncCommand SmartInsertCommand { get; private set; } = null!;
    public RelayCommand CancelOperationCommand { get; private set; } = null!;
    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (!SetProperty(ref _isProcessing, value)) return;
            OnPropertyChanged(nameof(CanUseCatalog));
            OnPropertyChanged(nameof(CanEditExport));
            NotifyActivity();
            NotifyCommands();
        }
    }
    public string DisplayStatus => IsProcessing ? _operationStatus : CatalogStatus;
    public double DisplayProgress => IsProcessing ? _operationProgress ?? 0 : CatalogProgress;
    public bool IsActivityIndeterminate => IsProcessing ? _operationProgress is null : CatalogIndeterminate;
    public bool IsActivityVisible => IsLoading || IsProcessing;
    public bool CanExport => !_disposed && !IsProcessing && !IsChoosingCandidate && ExportItems.Count > 0;

    private void InitializeWorkflowCommands(IExcelService? excel)
    {
        _excel = excel ?? new ExcelService();
        _requests = new(_search);
        ImportExcelCommand = new(ImportExcelAsync, () => CanUseCatalog, ShowUnhandledError);
        ExportExcelCommand = new(ExportExcelAsync, () => CanExport, ShowUnhandledError);
        SmartInsertCommand = new(SmartInsertAsync, () => CanUseCatalog, ShowUnhandledError);
        CancelOperationCommand = new(() => _operationCts?.Cancel(), () => !_disposed && IsProcessing && _operationCts is not null);
    }
    private void NotifyActivity()
    {
        OnPropertyChanged(nameof(DisplayStatus));
        OnPropertyChanged(nameof(DisplayProgress));
        OnPropertyChanged(nameof(IsActivityIndeterminate));
        OnPropertyChanged(nameof(IsActivityVisible));
    }
    private void NotifyWorkflowCommands()
    {
        OnPropertyChanged(nameof(CanExport));
        ImportExcelCommand?.NotifyCanExecuteChanged();
        ExportExcelCommand?.NotifyCanExecuteChanged();
        SmartInsertCommand?.NotifyCanExecuteChanged();
        CancelOperationCommand?.NotifyCanExecuteChanged();
    }
    private async Task RunOperationAsync(string message, Func<CancellationToken, IProgress<WorkflowProgress>, Task<string?>> action)
    {
        if (_disposed || IsProcessing) return;
        _searchRevision++;
        _searchCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationCts = cts;
        _operationStatus = message;
        _operationProgress = null;
        IsProcessing = true;
        var progress = new Progress<WorkflowProgress>(p =>
        {
            if (_disposed || !ReferenceEquals(_operationCts, cts) || cts.IsCancellationRequested) return;
            _operationStatus = p.Message;
            _operationProgress = p.Percent;
            NotifyActivity();
        });
        string? information = null;
        string? error = null;
        try { information = await action(cts.Token, progress); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { information = "Операция отменена."; }
        catch (OverflowException) { error = "Суммарное количество превышает допустимый размер целого числа. Проверьте исходные позиции."; }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            if (ReferenceEquals(_operationCts, cts)) _operationCts = null;
            if (!_disposed) IsProcessing = false;
            cts.Dispose();
        }
        if (_disposed) return;
        if (error is not null) _dialogs.ShowError(error);
        else if (information is not null) _dialogs.ShowInformation(information);
    }

    public Task ExportExcelAsync()
    {
        if (!CanExport) return Task.CompletedTask;
        return RunOperationAsync("Выгрузка Excel…", async (token, progress) =>
        {
            var path = _dialogs.SelectExportPath();
            if (path is null) return null;
            var rows = ExportItems.Select(i => new ExportRowSnapshot(i.Article, i.Quantity)).ToArray();
            progress.Report(new(null, "Сохранение Excel…"));
            await _excel.ExportAsync(path, rows, token);
            return "Файл сохранён:\n" + path;
        });
    }
    public Task ImportExcelAsync()
    {
        if (!CanUseCatalog) return Task.CompletedTask;
        return RunOperationAsync("Импорт Excel…", async (token, progress) =>
        {
            var path = _dialogs.OpenExcelFile();
            if (path is null) return null;
            progress.Report(new(null, "Чтение Excel…"));
            var sheets = await _excel.GetSheetNamesAsync(path, token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return null;
            var rows = _dialogs.EditExcel(path, sheets);
            if (rows is null) return null;
            if (rows.Count == 0) return "Не выбрана ни одна строка для импорта.";
            return await AddTableRowsAsync(rows, "Excel", progress, token);
        });
    }
    private async Task<string> AddTableRowsAsync(IReadOnlyList<RequestLine> rows, string source,
        IProgress<WorkflowProgress> progress, CancellationToken token)
    {
        var result = await _requests.PrepareTableAsync(_catalog!, rows, progress, token);
        token.ThrowIfCancellationRequested();
        if (_disposed) return "";
        ExportItems.AddRange(result.Items);
        var message = $"Источник: {source}\nСтрок получено: {result.InputCount}\nПосле объединения дублей: {result.MergedCount}\nНайдено в базе: {result.FoundCount}\nНет в базе: {result.MissingCount}";
        if (result.MissingLabels.Count > 0) message += "\n\nНет в базе:\n" + PreviewLines(result.MissingLabels);
        return message;
    }
    public Task SmartInsertAsync()
    {
        if (!CanUseCatalog) return Task.CompletedTask;
        return RunOperationAsync("Вставка заявки…", async (token, progress) =>
        {
            var request = _dialogs.RequestSmartInsert();
            if (request is null) return null;
            var plan = await _requests.PrepareTextAsync(_catalog!, request, progress, token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return null;
            if (plan.TableRows is not null)
                return plan.TableRows.Count == 0 ? "Не удалось разобрать таблицу. Проверьте, что скопирован весь блок."
                    : await AddTableRowsAsync(plan.TableRows, "вставленная таблица", progress, token);
            if (plan.Rows.Count == 0) return "Не удалось распознать товары. Проверьте формат.";
            var items = new List<ExportItem>();
            var skipped = new List<string>();
            var byArticle = 0;
            var strong = 0;
            var weak = 0;
            var missing = 0;
            var invalid = 0;
            foreach (var row in plan.Rows)
            {
                token.ThrowIfCancellationRequested();
                ExportItem? item = null;
                switch (row.State)
                {
                    case SmartRowState.Invalid:
                        invalid++;
                        skipped.Add(row.SourceText + " — некорректное количество");
                        break;
                    case SmartRowState.Article:
                        var product = row.ExactProduct!;
                        item = new(product.Article, row.Quantity, product.Name, product.Manufacturer, null, row.SourceText, SelectionOrigin.Article);
                        if (row.Assessment is not null) item.ApplyAssessment(row.Assessment);
                        byArticle++;
                        break;
                    case SmartRowState.StrictMissing:
                        missing++;
                        skipped.Add(row.SourceText);
                        if (request.KeepMissing)
                            item = new("АРТИКУЛ НЕ НАЙДЕН", row.Quantity, row.ArticleToken ?? row.SourceText, "", 0, row.SourceText, SelectionOrigin.Unmatched);
                        break;
                    case SmartRowState.Strong:
                        item = MatchedItem(row, row.Candidates[0], SelectionOrigin.Scored);
                        strong++;
                        break;
                    case SmartRowState.Weak:
                        var chosen = request.AskForWeakMatch ? _dialogs.ChooseCandidate(row.SourceText, row.Candidates) : null;
                        token.ThrowIfCancellationRequested();
                        item = MatchedItem(row, chosen ?? row.Candidates[0], chosen is null ? SelectionOrigin.Scored : SelectionOrigin.Alternative);
                        if (chosen is null) weak++; else strong++;
                        break;
                    case SmartRowState.Missing:
                        item = new("НЕ НАЙДЕНО", row.Quantity, row.SourceText, "", row.Candidates.Count > 0 ? row.Candidates[0].Score : 0,
                            row.SourceText, SelectionOrigin.Unmatched);
                        missing++;
                        break;
                }
                if (item is not null) { item.SetCandidates(row.Candidates); items.Add(item); }
            }
            token.ThrowIfCancellationRequested();
            if (_disposed) return null;
            ExportItems.AddRange(items);
            var message = $"Распознано позиций: {plan.Rows.Count}\nДобавлено: {items.Count}\nНайдено по артикулу: {byArticle}";
            if (request.Mode != SmartInsertMode.ArticlesOnly) message += $"\nУверенно по названию: {strong}\nТребует проверки: {weak}";
            message += $"\nНе найдено: {missing}";
            if (invalid > 0) message += $"\nПропущено с некорректным количеством: {invalid}";
            if (skipped.Count > 0) message += "\n\nНенайденные/пропущенные строки:\n" + PreviewLines(skipped);
            return message + "\n\nДвойной клик по строке — выбрать другой вариант.";
        });
    }
    private static ExportItem MatchedItem(SmartPlannedRow row, SearchResult result, SelectionOrigin origin)
    {
        var item = new ExportItem(result.Article, row.Quantity, result.Name, result.Product.Manufacturer, origin == SelectionOrigin.Alternative ? null : result.Score, row.SourceText, origin);
        item.ApplyAssessment(result);
        return item;
    }
    private static string PreviewLines(IReadOnlyList<string> lines) =>
        string.Join("\n", lines.Take(20).Select(s => "• " + s)) + (lines.Count > 20 ? $"\n… и ещё {lines.Count - 20}" : "");
}
