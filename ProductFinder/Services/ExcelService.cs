using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;
using ProductFinder.Models;

namespace ProductFinder.Services;

public sealed class ExcelService : IExcelService
{
    public Task<IReadOnlyList<string>> GetSheetNamesAsync(string path, CancellationToken token) => Task.Run<IReadOnlyList<string>>(() =>
    {
        token.ThrowIfCancellationRequested();
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var workbook = WorkbookFactory.Create(stream);
        token.ThrowIfCancellationRequested();
        return Enumerable.Range(0, workbook.NumberOfSheets).Select(workbook.GetSheetName).ToArray();
    }, token);

    public Task<ExcelSheetData> ReadSheetAsync(string path, string sheetName, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var workbook = WorkbookFactory.Create(stream);
        var sheet = workbook.GetSheet(sheetName) ?? throw new InvalidDataException("Лист не найден: " + sheetName);
        var maxColumn = 0;
        for (var r = 0; r <= sheet.LastRowNum; r++)
        {
            token.ThrowIfCancellationRequested();
            maxColumn = Math.Max(maxColumn, sheet.GetRow(r)?.LastCellNum ?? 0);
        }
        if ((long)(sheet.LastRowNum + 1) * maxColumn > 5000000)
            throw new InvalidDataException("В выбранном листе больше 5 миллионов ячеек. Удалите лишние пустые/форматированные строки и столбцы или разделите лист.");
        var rows = new List<object?[]>();
        if (sheet.PhysicalNumberOfRows > 0)
            for (var r = 0; r <= sheet.LastRowNum; r++)
            {
                token.ThrowIfCancellationRequested();
                var source = sheet.GetRow(r);
                var cells = new object?[maxColumn];
                for (var c = 0; c < maxColumn; c++) cells[c] = ReadCell(source?.GetCell(c, MissingCellPolicy.RETURN_BLANK_AS_NULL));
                rows.Add(cells);
            }
        return new ExcelSheetData(sheetName, rows);
    }, token);

    private static object? ReadCell(ICell? cell)
    {
        if (cell is null) return null;
        var type = cell.CellType;
        if (type == CellType.Formula)
        {
            if (cell is XSSFCell xssf && string.IsNullOrEmpty(xssf.GetRawValue())) return null;
            type = cell.CachedFormulaResultType;
        }
        return type switch
        {
            CellType.String => cell.StringCellValue,
            CellType.Numeric => DateUtil.IsCellDateFormatted(cell) ? cell.DateCellValue : cell.NumericCellValue,
            CellType.Boolean => cell.BooleanCellValue,
            CellType.Error => FormulaError.ForInt(cell.ErrorCellValue).String,
            _ => null
        };
    }

    public Task ExportAsync(string path, IReadOnlyList<ExportRowSnapshot> rows, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (rows.Count == 0) throw new InvalidOperationException("Добавьте товары для выгрузки.");
        if (rows.Count > 1048575) throw new InvalidOperationException("Слишком много строк для одного листа Excel.");
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Для выгрузки выберите формат .xlsx.");
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var workbook = new XSSFWorkbook())
            {
                var sheet = workbook.CreateSheet("Товары");
                var font = workbook.CreateFont();
                font.FontName = "Arial";
                font.FontHeightInPoints = 11;
                var headerFont = workbook.CreateFont();
                headerFont.FontName = "Arial";
                headerFont.FontHeightInPoints = 11;
                headerFont.IsBold = true;
                var headerStyle = workbook.CreateCellStyle();
                headerStyle.SetFont(headerFont);
                headerStyle.FillForegroundColor = IndexedColors.PaleBlue.Index;
                headerStyle.FillPattern = FillPattern.SolidForeground;
                headerStyle.VerticalAlignment = VerticalAlignment.Center;
                var articleStyle = workbook.CreateCellStyle();
                articleStyle.SetFont(font);
                articleStyle.DataFormat = workbook.CreateDataFormat().GetFormat("@");
                var quantityStyle = workbook.CreateCellStyle();
                quantityStyle.SetFont(font);
                quantityStyle.DataFormat = workbook.CreateDataFormat().GetFormat("0");
                quantityStyle.Alignment = HorizontalAlignment.Right;
                var header = sheet.CreateRow(0);
                header.HeightInPoints = 24;
                for (var c = 0; c < 2; c++)
                {
                    var cell = header.CreateCell(c, CellType.String);
                    cell.SetCellValue(c == 0 ? "Артикул" : "Количество");
                    cell.CellStyle = headerStyle;
                }
                for (var r = 0; r < rows.Count; r++)
                {
                    token.ThrowIfCancellationRequested();
                    if (rows[r].Quantity <= 0) throw new InvalidDataException($"Некорректное количество в строке {r + 1}.");
                    var row = sheet.CreateRow(r + 1);
                    row.HeightInPoints = 18;
                    var article = row.CreateCell(0, CellType.String);
                    article.SetCellValue(rows[r].Article);
                    article.CellStyle = articleStyle;
                    var quantity = row.CreateCell(1, CellType.Numeric);
                    quantity.SetCellValue(rows[r].Quantity);
                    quantity.CellStyle = quantityStyle;
                }
                sheet.SetColumnWidth(0, 32 * 256);
                sheet.SetColumnWidth(1, 16 * 256);
                sheet.CreateFreezePane(0, 1);
                sheet.SetAutoFilter(new CellRangeAddress(0, rows.Count, 0, 1));
                token.ThrowIfCancellationRequested();
                using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                workbook.Write(output, leaveOpen: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }, token);
}
