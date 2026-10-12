using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ProductFinder.Models;

namespace ProductFinder.Services;

public interface IExcelService
{
    Task<IReadOnlyList<string>> GetSheetNamesAsync(string path, CancellationToken token);
    Task<ExcelSheetData> ReadSheetAsync(string path, string sheetName, CancellationToken token);
    Task ExportAsync(string path, IReadOnlyList<ExportRowSnapshot> rows, CancellationToken token);
}
