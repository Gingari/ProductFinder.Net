using System.Collections.Generic;
using ProductFinder.Infrastructure;

namespace ProductFinder.Models;

public sealed record ExcelSheetData(string Name, IReadOnlyList<object?[]> Rows);
public sealed record ExcelColumn(int Index, string Label);
public sealed record ExcelColumnMapping(int HeaderRow, int ArticleColumn, int NameColumn, int QuantityColumn);

public sealed class ExcelImportRow : ObservableObject
{
    private bool _isSelected;
    private string _article;
    private string _name;
    private string _quantityText;
    public ExcelImportRow(int sourceRow, string columnLetter, string article, string name, string quantityText, string sourceText, bool isSelected)
    {
        SourceRow = sourceRow;
        ColumnLetter = columnLetter;
        _article = article;
        _name = name;
        _quantityText = quantityText;
        SourceText = sourceText;
        _isSelected = isSelected;
    }
    public int SourceRow { get; }
    public string ColumnLetter { get; }
    public string SourceText { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public string Article { get => _article; set => SetProperty(ref _article, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string QuantityText { get => _quantityText; set => SetProperty(ref _quantityText, value); }
}
