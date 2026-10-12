using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using ProductFinder.Models;

namespace ProductFinder.Services.Etm;

public sealed class EtmBrowserService : IEtmBrowserService, IEtmBrowserModeService
{
    private readonly EtmBrowserOptions _options;
    private readonly Func<Task<bool>>? _approveDownload;
    private readonly Func<IBrowserContext, Task>? _configureContext;
    private bool _background;
    private string? _lastUrl;
    public bool RunsInBackground => _background;
    public EtmBrowserService(EtmBrowserOptions? options = null, Func<Task<bool>>? approveDownload = null,
        Func<IBrowserContext, Task>? configureContext = null)
    {
        _options = options ?? new();
        _background = _options.Background;
        _approveDownload = approveDownload;
        _configureContext = configureContext;
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private bool _disposed;
    private readonly Dictionary<string, string> _resolvedUrls = new(StringComparer.Ordinal);
    private static string CandidateKey(EtmCandidate item) => item.Query + "\n" + item.SelectorKey + "\n" + item.Name;
    private async Task EnsureAsync(CancellationToken token, IProgress<string>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_page is not null && !_page.IsClosed) return;
        if (_context is not null)
        {
            var remaining = _context.Pages.FirstOrDefault(p => !p.IsClosed);
            if (remaining is not null) { _page = remaining; return; }
            try { await _context.CloseAsync(); } catch (PlaywrightException) { }
            _context = null;
        }
        _playwright ??= await Playwright.CreateAsync();
        var profile = _options.ProfileDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProductFinder", "EtmBrowser");
        var executable = await BrowserRuntime.ResolveAsync(_playwright, _options, _approveDownload, progress, token);
        progress?.Report(_background ? "Подготовка браузера без видимого окна…" : "Открытие браузера для ручной проверки…");
        Directory.CreateDirectory(profile);
        try
        {
            _context = await _playwright.Chromium.LaunchPersistentContextAsync(profile, new()
            {
                ExecutablePath = executable, Headless = _background,
                ViewportSize = new() { Width = 1280, Height = 850 }, Timeout = 20000
            });
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException("Не удалось запустить браузер для ЭТМ. Проверьте доступ к компоненту и закройте оставшееся от прошлого запуска окно ЭТМ.\n\n" + ex.Message, ex);
        }
        if (_configureContext is not null) await _configureContext(_context);
        _page = _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
        _page.SetDefaultTimeout(12000);
        _page.SetDefaultNavigationTimeout(20000);
        token.ThrowIfCancellationRequested();
        if (EtmUrl.IsAllowed(_lastUrl ?? "")) await _page.GotoAsync(_lastUrl!, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
    }
    private async Task<EtmSearchSnapshot> SearchSnapshotAsync()
    {
        var json = await _page!.EvaluateAsync<string>(EtmPageScripts.SearchSnapshot);
        return JsonSerializer.Deserialize<EtmSearchSnapshot>(json, JsonOptions) ?? new("", Array.Empty<EtmCandidate>(), "", false);
    }
    private async Task CheckAccessAsync()
    {
        var snapshot = await SearchSnapshotAsync();
        if (snapshot.Error.Length > 0) throw new EtmAccessDeniedException(snapshot.Error);
    }
    private async Task<IReadOnlyList<EtmCandidate>> SearchPageAsync(string query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!EtmUrl.IsAllowed(_page!.Url))
            await _page.GotoAsync("https://www.etm.ru/", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await CheckAccessAsync();
        var input = _page.Locator("input[data-testid='input-search']:visible").First;
        if (await input.CountAsync() == 0 && new Uri(_page.Url).AbsolutePath != "/")
        {
            await _page.GotoAsync("https://www.etm.ru/", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await CheckAccessAsync();
        }
        try { await input.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 12000 }); }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException("Поле поиска ЭТМ не появилось. Нажмите «Открыть браузер», закройте баннер или пройдите проверку сайта вручную, затем повторите поиск.\n\n" + ex.Message, ex);
        }
        var type = _page.Locator("#select-search-type-combobox:visible");
        if (await type.CountAsync() > 0 && !(await type.InnerTextAsync()).Contains("контексту", StringComparison.OrdinalIgnoreCase))
        {
            await type.ClickAsync();
            await _page.GetByRole(AriaRole.Option, new() { Name = "По контексту", Exact = true }).ClickAsync();
        }
        token.ThrowIfCancellationRequested();
        await input.ClickAsync();
        await input.FillAsync("");
        await Task.Delay(150, token);
        await input.FillAsync(query);
        var watch = Stopwatch.StartNew();
        var signature = "";
        var stableSince = TimeSpan.Zero;
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            token.ThrowIfCancellationRequested();
            var snapshot = await SearchSnapshotAsync();
            if (snapshot.Error.Length > 0) throw new EtmAccessDeniedException(snapshot.Error);
            if (snapshot.Query == query && snapshot.Items.Count > 0)
            {
                var next = string.Join("|", snapshot.Items.Select(i => i.SelectorKey + ":" + i.Name));
                if (next != signature) { signature = next; stableSince = watch.Elapsed; }
                else if (watch.Elapsed - stableSince >= TimeSpan.FromMilliseconds(700)) return snapshot.Items.Take(6).ToArray();
            }
            else if (snapshot.Query == query && snapshot.Empty) return Array.Empty<EtmCandidate>();
            await Task.Delay(200, token);
        }
        throw new InvalidOperationException("ЭТМ не показал поисковые подсказки. Проверьте запрос; при необходимости нажмите «Открыть браузер». Если сайт изменил разметку, понадобится обновление парсера.");
    }
    private async Task<string> FollowCandidateAsync(EtmCandidate candidate, CancellationToken token)
    {
        if (!Regex.IsMatch(candidate.SelectorKey, "^diginetica-main-descr-[0-9]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new InvalidOperationException("Некорректный идентификатор результата ЭТМ.");
        var snapshot = await SearchSnapshotAsync();
        if (snapshot.Error.Length > 0) throw new EtmAccessDeniedException(snapshot.Error);
        var current = snapshot.Query == candidate.Query ? snapshot.Items : Array.Empty<EtmCandidate>();
        if (!current.Any(c => c.SelectorKey == candidate.SelectorKey && c.Name == candidate.Name))
            current = await SearchPageAsync(candidate.Query, token);
        var actual = current.FirstOrDefault(c => c.SelectorKey == candidate.SelectorKey && c.Name == candidate.Name);
        if (actual is null) throw new InvalidOperationException("Результаты ЭТМ изменились. Выполните поиск ещё раз и выберите товар заново.");
        var previousUrl = _page!.Url;
        var pages = _context!.Pages.ToHashSet();
        await _page.Locator($"[data-testid='{candidate.SelectorKey}']:visible").ClickAsync();
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            token.ThrowIfCancellationRequested();
            var newPage = _context.Pages.FirstOrDefault(p => !pages.Contains(p) && EtmUrl.IsAllowed(p.Url));
            if (newPage is not null) _page = newPage;
            if (_page.Url != previousUrl && EtmUrl.IsAllowed(_page.Url))
            {
                await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                await CheckAccessAsync();
                return _page.Url;
            }
            await Task.Delay(200, token);
        }
        throw new InvalidOperationException("Карточка выбранного товара не открылась. Повторите поиск или проверьте страницу через «Открыть браузер».");
    }
    public async Task<IReadOnlyList<EtmCandidate>> SearchAsync(string query, IProgress<string>? progress, CancellationToken token)
    {
        query = query.Trim();
        if (query.Length is < 2 or > 500) throw new ArgumentException("Введите поисковый запрос длиной от 2 до 500 символов.");
        await _gate.WaitAsync(token);
        try
        {
            await EnsureAsync(token, progress);
            progress?.Report("Поиск на ЭТМ… При проверке сайта нажмите «Открыть браузер» и повторите поиск.");
            var items = await SearchPageAsync(query, token);
            token.ThrowIfCancellationRequested();
            return items.Select(item => !EtmUrl.IsAllowed(item.Url) && _resolvedUrls.TryGetValue(CandidateKey(item), out var url)
                ? item with { Url = url } : item).ToArray();
        }
        finally { _gate.Release(); }
    }
    public async Task<EtmProduct> ReadProductAsync(EtmCandidate candidate, IProgress<string>? progress, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await EnsureAsync(token, progress);
            progress?.Report("Открытие выбранной карточки ЭТМ…");
            var targetUrl = EtmUrl.IsAllowed(candidate.Url) ? candidate.Url
                : _resolvedUrls.GetValueOrDefault(CandidateKey(candidate), "");
            if (EtmUrl.IsAllowed(targetUrl))
            {
                if (_page!.Url != targetUrl)
                    await _page.GotoAsync(targetUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            }
            else await FollowCandidateAsync(candidate, token);
            await CheckAccessAsync();
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(15))
            {
                token.ThrowIfCancellationRequested();
                var json = await _page!.EvaluateAsync<string>(EtmPageScripts.ProductSnapshot);
                var product = JsonSerializer.Deserialize<EtmProduct>(json, JsonOptions);
                if (product is not null && product.Article.Length > 0 && product.Manufacturer.Length > 0)
                {
                    EtmUrl.Validate(product.Url);
                    var name = product.Name.Length > 0 ? product.Name : candidate.Name;
                    if (name.Length == 0) throw new InvalidOperationException("У карточки не найдено название товара.");
                    _resolvedUrls[CandidateKey(candidate)] = product.Url;
                    return product with { Name = name };
                }
                await Task.Delay(200, token);
            }
            throw new InvalidOperationException("В карточке ЭТМ не найдены строки «Артикул» и «Производитель». Ничего не сохранено. Проверьте страницу через «Открыть браузер»: разметка или доступ могли измениться.");
        }
        finally { _gate.Release(); }
    }
    public async Task SetBackgroundAsync(bool background, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_background == background) return;
            if (_page is not null && !_page.IsClosed && EtmUrl.IsAllowed(_page.Url)) _lastUrl = _page.Url;
            if (_context is not null)
            {
                try { await _context.CloseAsync(); } catch (PlaywrightException) { }
            }
            _context = null;
            _page = null;
            _background = background;
        }
        finally { _gate.Release(); }
    }
    public async Task ShowBrowserAsync(CancellationToken token)
    {
        await SetBackgroundAsync(false, token);
        await _gate.WaitAsync(token);
        try
        {
            await EnsureAsync(token);
            if (!EtmUrl.IsAllowed(_page!.Url)) await _page.GotoAsync("https://www.etm.ru/");
            await _page.BringToFrontAsync();
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_context is not null)
        {
            try { await _context.CloseAsync(); } catch (PlaywrightException) { }
        }
        _playwright?.Dispose();
    }
}
