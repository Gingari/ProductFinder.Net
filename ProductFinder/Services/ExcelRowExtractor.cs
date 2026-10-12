using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using ProductFinder.Models;

namespace ProductFinder.Services;

public static class ExcelRowExtractor
{
    public static string CellText(object? value) => value switch
    {
        null => "",
        bool b => b ? "True" : "False",
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture)?.Trim() ?? "",
        _ => value.ToString()?.Trim() ?? ""
    };
    public static int? ParseQuantity(object? value)
    {
        if (value is null or bool) return null;
        var text = CellText(value).Replace('\u00a0', ' ').Replace(" ", "").Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) return null;
        var rounded = Math.Round(number, MidpointRounding.ToEven);
        return rounded > 0 && rounded <= int.MaxValue ? (int)rounded : null;
    }
    public static string ColumnLetter(int index)
    {
        var output = "";
        while (index >= 0) { output = (char)('A' + index % 26) + output; index = index / 26 - 1; }
        return output;
    }
    private static bool HeaderMatch(string text, string pattern) => CatalogText.GetRegex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase).IsMatch(text);
    public static int DetectHeader(ExcelSheetData sheet)
    {
        for (var i = 0; i < Math.Min(30, sheet.Rows.Count); i++)
            if (sheet.Rows[i].Any(c => HeaderMatch(CellText(c), @"артикул|код|sku|номенклатурный"))) return i + 1;
        return 1;
    }
    public static IReadOnlyList<ExcelColumn> Columns(ExcelSheetData sheet, int headerRow)
    {
        if (headerRow < 1 || headerRow > sheet.Rows.Count) return Array.Empty<ExcelColumn>();
        return sheet.Rows[headerRow - 1].Select((v, i) => new ExcelColumn(i, ColumnLetter(i) + ". " + (CellText(v).Length > 0 ? CellText(v) : "(колонка)"))).ToArray();
    }
    public static ExcelColumnMapping DetectMapping(ExcelSheetData sheet, int headerRow)
    {
        var headers = headerRow > 0 && headerRow <= sheet.Rows.Count ? sheet.Rows[headerRow - 1].Select(CellText).ToArray() : Array.Empty<string>();
        var article = Array.FindIndex(headers, h => HeaderMatch(h, @"артикул|код|sku|номенклатурный"));
        if (article < 0) article = 0;
        var name = Array.FindIndex(headers, h => HeaderMatch(h, @"наимен|назв|товар|описан"));
        var quantity = Array.FindIndex(headers, h => HeaderMatch(h, @"кол-?во|количество|заказ|шт") && !HeaderMatch(h, @"кратн|сумм|цена|остат|склад"));
        if (quantity < 0) quantity = Array.FindIndex(headers, h => HeaderMatch(h, @"кол-?во|количество|заказ|шт"));
        if (quantity < 0) quantity = headers.Length > 0 ? Math.Min(headers.Length - 1, article + 2) : 0;
        return new(headerRow, article, name, quantity);
    }
    public static IReadOnlyList<ExcelImportRow> Extract(ExcelSheetData sheet, ExcelColumnMapping mapping, CancellationToken token = default)
    {
        var rows = new List<ExcelImportRow>();
        if (mapping.HeaderRow < 1 || mapping.HeaderRow > sheet.Rows.Count) return rows;
        for (var i = mapping.HeaderRow; i < sheet.Rows.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var cells = sheet.Rows[i];
            object? Get(int column) => column >= 0 && column < cells.Length ? cells[column] : null;
            var article = CellText(Get(mapping.ArticleColumn));
            var name = CellText(Get(mapping.NameColumn));
            var value = Get(mapping.QuantityColumn);
            var quantity = ParseQuantity(value);
            var quantityText = (quantity ?? 1).ToString(CultureInfo.InvariantCulture);
            var numeric = CellText(value).Replace(',', '.').Replace(" ", "");
            if (double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n > int.MaxValue) quantityText = CellText(value);
            var raw = string.Join(" | ", cells.Select(CellText).Where(t => t.Length > 0));
            if (raw.Length == 0) continue;
            var selected = (article.Length > 0 || name.Length > 0) && !HeaderMatch(article, @"артикул|код|sku|номенклатурный");
            rows.Add(new(i + 1, ColumnLetter(mapping.ArticleColumn), article, name, quantityText, raw, selected));
        }
        return rows;
    }
    public static IReadOnlyList<RequestLine> SelectedLines(IEnumerable<ExcelImportRow> rows)
    {
        var output = new List<RequestLine>();
        foreach (var row in rows.Where(r => r.IsSelected))
        {
            var article = row.Article.Trim();
            var name = row.Name.Trim();
            if (article.Length == 0 && name.Length == 0) continue;
            var text = row.QuantityText.Replace(" ", "");
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
                throw new InvalidDataException($"Проверьте количество в исходной строке {row.SourceRow}: нужно положительное целое число.");
            output.Add(new(article, name, quantity));
        }
        return output;
    }
}
