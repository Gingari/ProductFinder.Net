using System;
using System.Threading.Tasks;
using ProductFinder.Models;
using ProductFinder.Services;

namespace ProductFinder.ViewModels;

public sealed partial class MainWindowViewModel
{
    public Task ParseEtmAsync(ExportItem item)
    {
        var etmDialogs = _dialogs as IEtmDialogService;
        if (etmDialogs is null || !CanUseCatalog || !item.CanParseEtm || !ExportItems.Contains(item))
            return Task.CompletedTask;
        return RunOperationAsync("Допарсить на ЭТМ…", async (token, progress) =>
        {
            var saved = etmDialogs.ParseEtm(string.IsNullOrWhiteSpace(item.SourceText) ? item.Name : item.SourceText, _databasePath);
            if (saved is null) return null;
            if (_disposed) return null;
            item.ApplyEtmResult(saved);
            progress.Report(new(null, "Обновление индекса каталога…"));
            try
            {
                var refreshed = await _loader.LoadAsync(_databasePath, null, token);
                _catalog = refreshed.Index;
                OnPropertyChanged(nameof(IsCatalogReady));
                OnPropertyChanged(nameof(CanUseCatalog));
                SearchResults.Clear();
                SearchStatus = "Каталог обновлён после ЭТМ";
                CatalogStatus = $"Каталог загружен: {_catalog.Count:N0} товаров";
                CatalogDetails = _databasePath;
                HasCatalogError = false;
            }
            catch (Exception ex)
            {
                HasCatalogError = true; CatalogDetails = ex.Message;
                return "Запись сохранена/выбрана, строка заявки обновлена. Не удалось перечитать каталог: " + ex.Message + "\nНажмите «Повторить» для обновления индекса.";
            }
            var result = saved.Added ? "Новый товар добавлен в каталог и заявку." : saved.Replaced ? "Выбранная запись каталога заменена данными ЭТМ; заявка обновлена." : "Использована существующая запись каталога без изменения базы.";
            if (saved.BackupPath is not null) result += "\nРезервная копия: " + saved.BackupPath;
            return result + "\nРабочая база: " + _databasePath;
        });
    }
}
