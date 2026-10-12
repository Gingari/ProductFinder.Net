using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ProductFinder.Models;

namespace ProductFinder.Services;

internal sealed class MatchEvaluator
{
    internal static readonly Dictionary<string, string> SpecLabels = new(StringComparer.Ordinal)
    {
        ["product_type"] = "тип товара", ["current_a"] = "ток, А", ["poles"] = "полюса",
        ["ip"] = "IP", ["leakage_ma"] = "ток утечки, мА", ["breaking_ka"] = "отключающая способность, кА",
        ["curve"] = "характеристика", ["voltage_v"] = "напряжение, В", ["mount"] = "монтаж",
        ["seats"] = "число мест", ["cores"] = "число жил", ["section_mm2"] = "сечение, мм²", ["power_w"] = "мощность, Вт", ["color"] = "цвет"
    };
    private readonly CatalogIndex _index;
    private readonly string _normalized;
    private readonly string _query;
    private readonly string[] _terms;
    private readonly Dictionary<string, string> _fuzzy;
    private readonly Dictionary<string, double> _weights;
    private readonly string[] _brands;
    private readonly bool _hasArticle;
    internal Dictionary<string, string[]> Specs { get; }
    internal MatchEvaluator(CatalogIndex index, string query, string[] terms, Dictionary<string, string> fuzzy, string[] brands, bool hasArticle, string? description = null)
    {
        _index = index;
        _query = query;
        _normalized = SearchText.Normalize(description ?? query);
        Specs = SearchText.ExtractSpecs(description ?? query);
        _terms = terms;
        _fuzzy = fuzzy;
        _brands = brands;
        _hasArticle = hasArticle;
        _weights = terms.ToDictionary(t => t, t => Weight(index, t), StringComparer.Ordinal);
    }
    internal static double Weight(CatalogIndex index, string term)
    {
        var df = SearchText.RetrievalVariants(term).Where(index.Postings.ContainsKey).Select(t => index.Postings[t].Count).DefaultIfEmpty(0).Max();
        return Math.Clamp(1 + Math.Log(1 + index.Count / (1.0 + df)), 1, 4) * (term.Any(char.IsDigit) ? 1.35 : 1);
    }
    internal SearchResult Evaluate(int id, bool exact, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var product = _index.Products[id];
        var document = SearchText.Document(product);
        var reasons = new List<string>();
        var conflicts = new List<string>();
        var missing = new List<string>();
        var matches = 0;
        foreach (var (key, wanted) in Specs)
        {
            var label = SpecLabels.GetValueOrDefault(key, key);
            if (!document.Specs.TryGetValue(key, out var actual)) { missing.Add(label); continue; }
            if (wanted.All(v => actual.Contains(v, StringComparer.Ordinal))) { matches++; continue; }
            conflicts.Add($"{label}: нужно {string.Join("/", wanted)}, в каталоге {string.Join("/", actual)}");
        }
        var totalWeight = _weights.Values.Sum();
        var coveredWeight = 0.0;
        var fuzzyUsed = new List<string>();
        foreach (var term in _terms)
        {
            if (document.Tokens.Contains(term)) coveredWeight += _weights[term];
            else if (_fuzzy.TryGetValue(term, out var replacement) && document.Tokens.Contains(SearchText.Normalize(replacement)))
            {
                coveredWeight += _weights[term] * .82;
                fuzzyUsed.Add(term + " → " + replacement);
            }
        }
        var coverage = totalWeight > 0 ? coveredWeight / totalWeight : Specs.Count > 0 ? 1 : 0;
        var agreement = Specs.Count > 0 ? matches / (double)Specs.Count : coverage;
        var similarity = new SequenceMatcher(_normalized, document.NormalizedName, token).Ratio();
        var score = exact ? 1 : Math.Clamp(.74 * coverage + .18 * agreement + .08 * similarity, 0, .99);
        var review = false;
        if (exact)
        {
            reasons.Add("Точный артикул найден в каталоге.");
            var articleTerms = CatalogText.Tokenize(SearchText.Normalize(product.Article)).ToHashSet(StringComparer.Ordinal);
            var articleKey = SearchText.Normalize(product.Article).Replace("-", "").Replace(" ", "");
            var descriptionTerms = _terms.Where(t => !articleTerms.Contains(t) && t.Replace("-", "") != articleKey).ToArray();
            if (descriptionTerms.Any(t => !document.Tokens.Contains(t) && !(_fuzzy.TryGetValue(t, out var corrected) && document.Tokens.Contains(SearchText.Normalize(corrected)))))
            {
                score = Math.Min(score, MatchQuality.MaxReviewScore);
                review = true;
                reasons.Add("Артикул найден, но часть описания не подтверждена словами каталога.");
            }
        }
        else reasons.Add($"Покрытие слов запроса: {MatchQuality.Percent(coverage)}.");
        if (matches > 0) reasons.Add($"Подтверждено характеристик: {matches}/{Specs.Count}.");
        if (fuzzyUsed.Count > 0) reasons.Add("Исправление опечаток: " + string.Join(", ", fuzzyUsed) + ".");
        if (missing.Count > 0)
        {
            score = Math.Min(score, MatchQuality.MaxReviewScore);
            review = true;
            reasons.Add("Не указано в каталоге: " + string.Join(", ", missing) + ".");
        }
        if (conflicts.Count > 0)
        {
            score = Math.Min(score * .55, MatchQuality.MaxConflictScore);
            reasons.Add("Противоречия: " + string.Join("; ", conflicts) + ".");
        }
        var manufacturer = SearchText.Normalize(product.ManufacturerRaw.Replace('|', ' '));
        if (_brands.Length > 0 && !_brands.Any(b => CatalogText.Tokenize(manufacturer).Contains(b, StringComparer.Ordinal)))
        {
            score = Math.Min(score, MatchQuality.MaxReviewScore);
            review = true;
            reasons.Add("Запрошенный производитель не подтверждён.");
        }
        if (!exact && _hasArticle)
        {
            score = Math.Min(score, MatchQuality.MaxReviewScore);
            review = true;
            reasons.Add("Другой артикул: замену нужно подтвердить.");
        }
        if (!exact && Specs.Count <= 1 && !_terms.Any(t => !SearchText.GenericWords.Contains(t)) && _brands.Length == 0)
        {
            score = Math.Min(score, MatchQuality.MaxReviewScore);
            review = true;
            reasons.Add("Слишком общий запрос: уточните характеристики.");
        }
        return new(id, product, score)
        {
            IsExactArticle = exact, HasSpecConflict = conflicts.Count > 0, NeedsReview = review,
            Explanation = string.Join("\n", reasons), SourceQuery = _query
        };
    }
}
