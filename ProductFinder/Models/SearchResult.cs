using System;

namespace ProductFinder.Models;

public sealed record SearchResult(int RowId, CatalogProduct Product, double Score)
{
    public string Article => Product.Article;
    public string Name => Product.Name;
    public string Manufacturer => string.IsNullOrEmpty(Product.Manufacturer) ? "—" : Product.Manufacturer;
    public double? UnguardedScore { get; init; }
    public double OriginalScore => UnguardedScore ?? Score;
    public bool IsExactArticle { get; init; }
    public bool HasSpecConflict { get; init; }
    public bool NeedsReview { get; init; }
    public string Explanation { get; init; } = "";
    public string SourceQuery { get; init; } = "";
    public ConfidenceState DisplayConfidence => HasSpecConflict ? ConfidenceState.Missing : NeedsReview ? (MatchQuality.Classify(Score) == ConfidenceState.Missing ? ConfidenceState.Missing : ConfidenceState.Weak) : MatchQuality.Classify(Score);
    public ConfidenceState Confidence => DisplayConfidence;
    public bool CanAutoSelect => Score >= .62 && !NeedsReview;
    public string MatchText => MatchQuality.Percent(Score);
    public string MatchLabel => HasSpecConflict ? "Есть противоречия" : NeedsReview ? "Нужно проверить" : MatchQuality.Label(DisplayConfidence);
    public string MatchExplanation => Explanation.Length > 0 ? Explanation : "Оценка поиска: " + MatchText + ". Цвет не меняет подбор.";
}
