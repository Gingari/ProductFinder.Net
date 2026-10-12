using System.Collections.Generic;

namespace ProductFinder.Models;

public sealed record RequestLine(string Article, string Name, int Quantity);
public sealed record TextRequestItem(string SourceText, int Quantity);
public sealed record ExportRowSnapshot(string Article, int Quantity);
public sealed record WorkflowProgress(int? Percent, string Message);
public enum SmartInsertMode { Auto, ArticlesOnly, NamesOnly, PriceTable }
public sealed record SmartInsertRequest(string Text, SmartInsertMode Mode, bool AskForWeakMatch, bool KeepMissing);
public sealed record SmartInsertModeOption(SmartInsertMode Value, string Label);
public enum SmartRowState { Article, Strong, Weak, Missing, StrictMissing, Invalid }
public sealed record SmartPlannedRow(string SourceText, int Quantity, SmartRowState State,
    CatalogProduct? ExactProduct, string? ArticleToken, IReadOnlyList<SearchResult> Candidates)
{
    public SearchResult? Assessment { get; init; }
}
public sealed record SmartInsertPlan(IReadOnlyList<SmartPlannedRow> Rows, IReadOnlyList<RequestLine>? TableRows);
public sealed record RequestImportResult(IReadOnlyList<ExportItem> Items, int InputCount, int MergedCount,
    int FoundCount, int MissingCount, IReadOnlyList<string> MissingLabels);
