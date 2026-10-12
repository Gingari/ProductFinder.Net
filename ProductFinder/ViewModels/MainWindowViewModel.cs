using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProductFinder.Infrastructure;
using ProductFinder.Models;
using ProductFinder.Services;

namespace ProductFinder.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ICatalogLoader _loader;
    private readonly IProductSearchService _search;
    private readonly IDialogService _dialogs;
    private string _databasePath;
    private readonly bool _usePersistentCatalog;
    private readonly CancellationTokenSource _lifetime = new();
    private CatalogIndex? _catalog;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _searchCts;
    private IReadOnlyList<ExportItem> _selectedExportItems = Array.Empty<ExportItem>();
    private bool _initialized;
    private bool _disposed;
    private bool _isLoading;
    private bool _isSearching;
    private bool _isChoosingCandidate;
    private bool _hasCatalogError;
    private bool _catalogIndeterminate;
    private int _searchRevision;
    private double _catalogProgress;
    private string _catalogStatus = "Каталог не загружен";
    private string _catalogDetails = "";
    private string _searchQuery = "";
    private string _searchStatus = "Двойной клик — добавить в заявку";
    private string _manualArticle = "";
    private string _manualQuantity = "1";

    public MainWindowViewModel(ICatalogLoader loader, IProductSearchService search, IDialogService dialogs, string? databasePath = null, IExcelService? excel = null)
    {
        _loader = loader;
        _search = search;
        _dialogs = dialogs;
        _databasePath = databasePath ?? CatalogPathResolver.Resolve();
        _usePersistentCatalog = databasePath is null && loader is CatalogLoader;
        SearchCommand = new(SearchAsync, () => CanUseCatalog && !string.IsNullOrWhiteSpace(SearchQuery), ShowUnhandledError);
        ReloadCatalogCommand = new(LoadCatalogAsync, () => !_disposed && !IsLoading && !IsChoosingCandidate && !IsProcessing, ShowUnhandledError);
        ManualAddCommand = new(AddManualItem, () => CanUseCatalog && !string.IsNullOrWhiteSpace(ManualArticle));
        DeleteSelectedCommand = new(DeleteSelected, () => !_disposed && !IsChoosingCandidate && !IsProcessing && _selectedExportItems.Count > 0);
        InitializeWorkflowCommands(excel);
        ExportItems.CollectionChanged += (_, _) =>
        {
            _selectedExportItems = _selectedExportItems.Where(ExportItems.Contains).ToArray();
            NotifyCommands();
        };
    }

    public ObservableCollection<SearchResult> SearchResults { get; } = new();
    public RangeObservableCollection<ExportItem> ExportItems { get; } = new();
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand ReloadCatalogCommand { get; }
    public RelayCommand ManualAddCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }
    public bool IsCatalogReady => _catalog is not null;
    public bool CanUseCatalog => !_disposed && IsCatalogReady && !IsLoading && !IsProcessing && !IsChoosingCandidate;
    public bool CanEditExport => !_disposed && !IsChoosingCandidate && !IsProcessing;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetProperty(ref _isLoading, value)) return;
            OnPropertyChanged(nameof(CanUseCatalog));
            NotifyActivity();
            NotifyCommands();
        }
    }
    public bool IsSearching { get => _isSearching; private set => SetProperty(ref _isSearching, value); }
    public bool IsChoosingCandidate
    {
        get => _isChoosingCandidate;
        private set
        {
            if (!SetProperty(ref _isChoosingCandidate, value)) return;
            OnPropertyChanged(nameof(CanEditExport));
            OnPropertyChanged(nameof(CanUseCatalog));
            NotifyCommands();
        }
    }
    public bool HasCatalogError { get => _hasCatalogError; private set => SetProperty(ref _hasCatalogError, value); }
    public bool CatalogIndeterminate { get => _catalogIndeterminate; private set { if (SetProperty(ref _catalogIndeterminate, value)) NotifyActivity(); } }
    public double CatalogProgress { get => _catalogProgress; private set { if (SetProperty(ref _catalogProgress, value)) NotifyActivity(); } }
    public string CatalogStatus { get => _catalogStatus; private set { if (SetProperty(ref _catalogStatus, value)) NotifyActivity(); } }
    public string CatalogDetails { get => _catalogDetails; private set => SetProperty(ref _catalogDetails, value); }
    public string SearchStatus { get => _searchStatus; private set => SetProperty(ref _searchStatus, value); }
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (!SetProperty(ref _searchQuery, value)) return;
            _searchRevision++;
            _searchCts?.Cancel();
            SearchStatus = SearchResults.Count > 0 ? "Запрос изменён — нажмите «Найти»" : "Двойной клик — добавить в заявку";
            SearchCommand.NotifyCanExecuteChanged();
        }
    }
    public string ManualArticle
    {
        get => _manualArticle;
        set { if (SetProperty(ref _manualArticle, value)) ManualAddCommand.NotifyCanExecuteChanged(); }
    }
    public string ManualQuantity { get => _manualQuantity; set => SetProperty(ref _manualQuantity, value); }

    public Task InitializeAsync()
    {
        if (_initialized || _disposed) return Task.CompletedTask;
        _initialized = true;
        return LoadCatalogAsync();
    }

    public async Task LoadCatalogAsync()
    {
        if (_disposed || IsLoading || IsChoosingCandidate || IsProcessing) return;
        _searchRevision++;
        _searchCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loadCts = cts;
        IsLoading = true;
        HasCatalogError = false;
        CatalogDetails = "";
        CatalogStatus = "Загрузка каталога…";
        CatalogProgress = 0;
        CatalogIndeterminate = true;
        var progress = new Progress<CatalogLoadProgress>(p =>
        {
            if (_disposed || !ReferenceEquals(_loadCts, cts) || cts.IsCancellationRequested) return;
            CatalogStatus = p.Message;
            CatalogIndeterminate = p.Percent is null;
            if (p.Percent is int percent) CatalogProgress = percent;
        });
        string? error = null;
        try
        {
            if (_usePersistentCatalog) _databasePath = await WritableCatalogStorage.EnsureAsync(_databasePath, token: cts.Token);
            var result = await _loader.LoadAsync(_databasePath, progress, cts.Token);
            if (_disposed || cts.IsCancellationRequested) return;
            _catalog = result.Index;
            OnPropertyChanged(nameof(IsCatalogReady));
            OnPropertyChanged(nameof(CanUseCatalog));
            SearchResults.Clear();
            SearchStatus = "Двойной клик — добавить в заявку";
            CatalogProgress = 100;
            var count = _catalog.Count.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"));
            CatalogStatus = $"Каталог загружен: {count} товаров";
            CatalogDetails = result.CacheWarning is not null ? "Каталог загружен, но кэш не сохранён: " + result.CacheWarning
                : result.FromCache ? "Использован подготовленный кэш" : _databasePath;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_disposed) return;
            HasCatalogError = true;
            CatalogStatus = _catalog is null ? "Ошибка загрузки каталога" : "Ошибка обновления каталога";
            CatalogDetails = ex.Message;
            error = "Не удалось загрузить каталог:\n\n" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) _loadCts = null;
            if (!_disposed) { CatalogIndeterminate = false; IsLoading = false; }
            cts.Dispose();
        }
        if (error is not null && !_disposed) _dialogs.ShowError(error);
    }

    public async Task SearchAsync()
    {
        if (!CanUseCatalog || string.IsNullOrWhiteSpace(SearchQuery)) return;
        var query = SearchQuery.Trim();
        var revision = ++_searchRevision;
        _searchCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _searchCts = cts;
        IsSearching = true;
        SearchStatus = "Поиск…";
        try
        {
            var results = await _search.SearchAsync(_catalog!, query, 200, cts.Token);
            if (_disposed || cts.IsCancellationRequested || revision != _searchRevision) return;
            SearchResults.Clear();
            foreach (var result in results) SearchResults.Add(result);
            SearchStatus = results.Count == 0 ? "По вашему запросу ничего не найдено" : "Двойной клик — добавить в заявку";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_disposed || revision != _searchRevision) return;
            SearchStatus = "Ошибка поиска";
            _dialogs.ShowError("Не удалось выполнить поиск:\n\n" + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
                if (!_disposed) IsSearching = false;
            }
            cts.Dispose();
        }
    }

    public void AddSearchResult(SearchResult result)
    {
        if (!CanUseCatalog) return;
        var quantity = _dialogs.RequestQuantity(result.Name);
        if (_disposed || quantity is null) return;
        var item = new ExportItem(result.Article, quantity.Value, result.Name, result.Product.Manufacturer,
            1, result.SourceQuery.Length > 0 ? result.SourceQuery : result.Name, SelectionOrigin.Search);
        item.ApplyAssessment(result);
        item.SetCandidates(SearchResults.ToArray());
        ExportItems.Add(item);
    }

    public void AddManualItem()
    {
        if (!CanUseCatalog) return;
        var article = ManualArticle.Trim();
        if (article.Length == 0) return;
        if (!int.TryParse(ManualQuantity.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity) || quantity is < 1 or > 100000)
        {
            _dialogs.ShowInformation("Введите целое количество от 1 до 100000.");
            return;
        }
        var id = _search.LookupArticle(_catalog!, article);
        var product = id is int rowId ? _catalog!.Products[rowId] : null;
        var item = new ExportItem(article, quantity, product?.Name ?? "Товар добавлен вручную", product?.Manufacturer ?? "",
            product is null ? 0 : 1, article, product is null ? SelectionOrigin.Manual : SelectionOrigin.Article);
        if (id is int exactId) item.ApplyAssessment(ProductMatchGuard.Evaluate(new(exactId, _catalog!.Products[exactId], 1) { IsExactArticle = true }, article));
        ExportItems.Add(item);
        ManualArticle = "";
        ManualQuantity = "1";
    }

    public void SetSelectedExportItems(IReadOnlyList<ExportItem> items)
    {
        _selectedExportItems = items.Where(ExportItems.Contains).ToArray();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private void DeleteSelected()
    {
        foreach (var item in _selectedExportItems.ToArray()) ExportItems.Remove(item);
        SetSelectedExportItems(Array.Empty<ExportItem>());
    }

    public async Task ChooseAlternativeAsync(ExportItem item)
    {
        if (!CanUseCatalog || IsChoosingCandidate || !ExportItems.Contains(item)) return;
        IsChoosingCandidate = true;
        try
        {
            var candidates = item.Candidates;
            if (candidates.Count == 0)
            {
                candidates = await _search.FindCandidatesAsync(_catalog!, item.SourceText, 30, _lifetime.Token);
                if (_disposed || !ExportItems.Contains(item)) return;
                item.SetCandidates(candidates);
            }
            if (candidates.Count == 0)
            {
                _dialogs.ShowInformation("Похожих товаров не нашлось.");
                return;
            }
            var selected = _dialogs.ChooseCandidate(item.SourceText, candidates);
            if (!_disposed && selected is not null && ExportItems.Contains(item)) item.SelectAlternative(selected);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { if (!_disposed) _dialogs.ShowError("Не удалось подобрать варианты:\n\n" + ex.Message); }
        finally { if (!_disposed) IsChoosingCandidate = false; }
    }

    private void NotifyCommands()
    {
        SearchCommand.NotifyCanExecuteChanged();
        ReloadCatalogCommand.NotifyCanExecuteChanged();
        ManualAddCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        NotifyWorkflowCommands();
    }
    private void ShowUnhandledError(Exception ex) { if (!_disposed) _dialogs.ShowError(ex.Message); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        NotifyCommands();
    }
}
