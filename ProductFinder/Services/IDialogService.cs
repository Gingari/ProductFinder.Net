using System.Collections.Generic;
using ProductFinder.Models;

namespace ProductFinder.Services;

public interface IDialogService
{
    void ShowError(string message);
    void ShowInformation(string message);
    string? OpenExcelFile();
    string? SelectExportPath();
    SmartInsertRequest? RequestSmartInsert();
    IReadOnlyList<RequestLine>? EditExcel(string path, IReadOnlyList<string> sheets);
    int? RequestQuantity(string productName);
    SearchResult? ChooseCandidate(string sourceText, IReadOnlyList<SearchResult> candidates);
}
