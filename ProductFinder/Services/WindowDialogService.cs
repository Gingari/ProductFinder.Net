using System.Collections.Generic;
using System.Windows;
using Microsoft.Win32;
using ProductFinder.Models;
using ProductFinder.Views;

namespace ProductFinder.Services;

public sealed class WindowDialogService(Window owner, IExcelService excel) : IDialogService, IEtmDialogService
{
    public void ShowError(string message) => MessageBox.Show(owner, message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
    public void ShowInformation(string message) => MessageBox.Show(owner, message, "Product Finder", MessageBoxButton.OK, MessageBoxImage.Information);
    public string? OpenExcelFile()
    {
        var dialog = new OpenFileDialog { Title = "Выберите файл заявки", Filter = "Excel (*.xlsx;*.xlsm;*.xls)|*.xlsx;*.xlsm;*.xls", CheckFileExists = true };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
    public string? SelectExportPath()
    {
        var dialog = new SaveFileDialog { Title = "Сохранить заявку", Filter = "Excel (*.xlsx)|*.xlsx", DefaultExt = ".xlsx", AddExtension = true, FileName = "Заказ.xlsx", OverwritePrompt = true };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
    public SmartInsertRequest? RequestSmartInsert()
    {
        var dialog = new SmartInsertWindow { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Request : null;
    }
    public IReadOnlyList<RequestLine>? EditExcel(string path, IReadOnlyList<string> sheets)
    {
        var dialog = new ExcelImportWindow(path, sheets, excel) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
    public int? RequestQuantity(string productName)
    {
        var dialog = new QuantityWindow(productName) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Quantity : null;
    }
    public SearchResult? ChooseCandidate(string sourceText, IReadOnlyList<SearchResult> candidates)
    {
        var dialog = new CandidateWindow(sourceText, candidates) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.SelectedResult : null;
    }
    public EtmSaveResult? ParseEtm(string sourceText, string databasePath)
    {
        var dialog = new EtmImportWindow(sourceText, databasePath) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Result;
    }

}
