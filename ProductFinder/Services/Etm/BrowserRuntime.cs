using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using ProductFinder.Models;

namespace ProductFinder.Services.Etm;

public static class BrowserRuntime
{
    private static readonly object InstallGate = new();
    private static Task<int>? _installTask;
    public static async Task<string> ResolveAsync(IPlaywright playwright, EtmBrowserOptions options,
        Func<Task<bool>>? approveDownload, IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            if (!File.Exists(options.ExecutablePath)) throw new FileNotFoundException("Не найден указанный браузер.", options.ExecutablePath);
            return options.ExecutablePath;
        }
        var bundled = playwright.Chromium.ExecutablePath;
        if (File.Exists(bundled)) return bundled;
        if (options.UseInstalledBrowsers && OperatingSystem.IsWindows())
        {
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
            var relatives = new[] { Path.Combine("Google", "Chrome", "Application", "chrome.exe"), Path.Combine("Microsoft", "Edge", "Application", "msedge.exe") };
            foreach (var relative in relatives)
            {
                var installed = roots.Where(p => p.Length > 0).Select(p => Path.Combine(p, relative)).FirstOrDefault(File.Exists);
                if (installed is not null) return installed;
            }
        }
        Task<int>? pending;
        lock (InstallGate) pending = _installTask is { IsCompleted: false } ? _installTask : null;
        if (pending is null)
        {
            if (approveDownload is null || !await approveDownload())
                throw new InvalidOperationException("Для ЭТМ нужен Chromium, Chrome или Edge. Загрузка Chromium не подтверждена. Установите компонент либо используйте доступный установленный браузер.");
            token.ThrowIfCancellationRequested();
            lock (InstallGate)
            {
                if (_installTask is null || _installTask.IsCompleted) _installTask = Task.Run(() => Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }));
                pending = _installTask;
            }
        }
        progress?.Report("Первый запуск: загрузка Chromium. Это может занять несколько минут. Отмена поиска не прерывает уже подтверждённую загрузку компонента.");
        var code = await pending.WaitAsync(token);
        if (code != 0 || !File.Exists(bundled))
            throw new InvalidOperationException("Не удалось загрузить Chromium. Проверьте доступ к серверам загрузки и свободное место. Нужен установленный Chromium, Chrome или Edge; обход сетевых ограничений не выполняется.");
        return bundled;
    }
}
