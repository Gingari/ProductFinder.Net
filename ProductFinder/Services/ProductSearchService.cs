using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProductFinder.Models;

namespace ProductFinder.Services;

public sealed class ProductSearchService : IProductSearchService
{
    private static readonly (string Key, double Weight)[] SpecWeights =
    {
        ("product_type", .20), ("current_a", .24), ("poles", .22), ("ip", .18),
        ("leakage_ma", .22), ("breaking_ka", .16), ("curve", .14), ("voltage_v", .10), ("mount", .12)
    };

    public int? LookupArticle(CatalogIndex index, string article, bool strict = false)
    {
        var key = CatalogText.Normalize(article).Replace(" ", "");
        int? TryKey(string candidate)
        {
            if (candidate.Length < 5) return null;
            if (index.ByArticle.TryGetValue(candidate, out var id)) return id;
            return index.ByArticleNoDash.TryGetValue(candidate.Replace("-", ""), out id) ? id : null;
        }
        var found = TryKey(key);
        if (found is not null || strict) return found;
        for (var cut = 1; cut < 9 && key.Length - cut >= 5; cut++)
        {
            var candidate = key[cut..];
            if (!char.IsLetter(candidate[0])) continue;
            found = TryKey(candidate);
            if (found is not null) return found;
        }
        for (var cut = 1; cut < 5 && cut <= key.Length; cut++)
        {
            found = TryKey(key[..^cut]);
            if (found is not null) return found;
        }
        return null;
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(CatalogIndex index, string query, int limit, CancellationToken token) =>
        Task.Run<IReadOnlyList<SearchResult>>(() =>
        {
            token.ThrowIfCancellationRequested();
            var exact = LookupArticle(index, query);
            if (exact is null)
                foreach (var candidate in CatalogText.ExtractArticleCandidates(query))
                {
                    exact = LookupArticle(index, candidate);
                    if (exact is not null) break;
                }
            var results = Match(index, query, limit, token).ToList();
            if (exact is int rowId)
            {
                var product = index.Products[rowId];
                results.RemoveAll(r => r.Article == product.Article);
                results.Insert(0, ProductMatchGuard.Evaluate(new(rowId, product, 1) { IsExactArticle = true }, query));
            }
            return OrderByManufacturer(results).ToArray();
        }, token);

    public Task<IReadOnlyList<SearchResult>> FindCandidatesAsync(CatalogIndex index, string query, int limit, CancellationToken token) =>
        Task.Run<IReadOnlyList<SearchResult>>(() => Match(index, query, limit, token), token);

    internal IReadOnlyList<SearchResult> Match(CatalogIndex index, string query, int limit, CancellationToken token)
    {
        var normalized = CatalogText.Normalize(query);
        if (normalized.Length == 0 || limit <= 0) return Array.Empty<SearchResult>();
        var queryTokens = CatalogText.Tokenize(normalized);
        var codes = CatalogText.ExtractCodes(normalized);
        var specs = CatalogText.ExtractSpecs(query);
        var expanded = new HashSet<string>(queryTokens, StringComparer.Ordinal);
        var fuzzy = new Dictionary<string, string>(StringComparer.Ordinal);
        var unknown = codes.Concat(queryTokens.OrderByDescending(t => t.Length).ThenBy(t => t, StringComparer.Ordinal))
            .Where(t => DocumentFrequency(index, t) == 0).Take(3);
        foreach (var term in unknown)
        {
            token.ThrowIfCancellationRequested();
            var alternative = FuzzyToken(index, term, token);
            if (alternative is null) continue;
            expanded.Add(alternative);
            fuzzy[term] = alternative;
        }
        var effectiveCodes = codes.Select(c => fuzzy.GetValueOrDefault(c, c)).ToArray();
        var candidates = CandidateIds(index, expanded, effectiveCodes, token);
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var term in expanded.Order(StringComparer.Ordinal))
        {
            var df = DocumentFrequency(index, term);
            var weight = df == 0 ? 0 : Math.Max(.15, Math.Sqrt(index.Count / (df + 1.0)) / (Math.Sqrt(index.Count) * 8));
            if (effectiveCodes.Contains(term, StringComparer.Ordinal)) weight *= 3;
            else if (term.Any(char.IsDigit)) weight *= 1.6;
            weights[term] = weight;
        }
        var totalWeight = weights.Values.Sum();
        if (totalWeight == 0) totalWeight = 1;
        var requestedBrand = new[] { "IEK", "EKF" }.FirstOrDefault(b => query.Contains(b, StringComparison.OrdinalIgnoreCase));
        var preliminary = new List<(double Score, int RowId)>();
        foreach (var rowId in candidates.Order())
        {
            token.ThrowIfCancellationRequested();
            var product = index.Products[rowId];
            if (product.Name.Length == 0) continue;
            var documentTokens = new HashSet<string>(product.SearchTokens, StringComparer.Ordinal);
            var score = weights.Where(w => documentTokens.Contains(w.Key)).Sum(w => w.Value) / totalWeight;
            if (effectiveCodes.Length > 0)
            {
                var hits = effectiveCodes.Count(c => documentTokens.Contains(c) || product.NormalizedName.Contains(c, StringComparison.Ordinal));
                score += .30 * hits / effectiveCodes.Length;
                if (hits == 0) score -= .18;
            }
            score -= Math.Min(Math.Abs(documentTokens.Count - queryTokens.Length) * .006, .12);
            if (requestedBrand is not null)
                score += product.ManufacturerRaw.Contains(requestedBrand, StringComparison.OrdinalIgnoreCase) ? .20 : -.20;
            foreach (var (key, weight) in SpecWeights)
            {
                if (!specs.TryGetValue(key, out var wanted) || !product.Specs.TryGetValue(key, out var actual)) continue;
                score += wanted.Intersect(actual, StringComparer.Ordinal).Any() ? weight : -weight * 1.6;
            }
            preliminary.Add((score, rowId));
        }
        var rerankLimit = (int)Math.Min(preliminary.Count, Math.Max(300L, (long)limit * 5));
        var results = new List<SearchResult>();
        foreach (var (score, rowId) in preliminary.OrderByDescending(r => r.Score).ThenBy(r => r.RowId).Take(rerankLimit))
        {
            token.ThrowIfCancellationRequested();
            var product = index.Products[rowId];
            var finalScore = score + .12 * new SequenceMatcher(normalized, product.NormalizedName, token).Ratio();
            results.Add(ProductMatchGuard.Evaluate(new(rowId, product, Math.Clamp(finalScore, 0, .99)), query));
        }
        return OrderByManufacturer(results.OrderByDescending(r => r.Score).Take(limit)).ToArray();
    }

    private static int DocumentFrequency(CatalogIndex index, string term) => index.Postings.TryGetValue(term, out var ids) ? ids.Count : 0;
    private static IEnumerable<SearchResult> OrderByManufacturer(IEnumerable<SearchResult> results) =>
        results.OrderBy(r => CatalogText.ManufacturerRank(r.Product.Manufacturer)).ThenByDescending(r => r.Score);
    private static string? FuzzyToken(CatalogIndex index, string term, CancellationToken token)
    {
        if (term.Length < 5) return null;
        var matcher = new SequenceMatcher("", term, token);
        string? best = null;
        var bestScore = .84;
        foreach (var candidate in index.Vocabulary)
        {
            token.ThrowIfCancellationRequested();
            matcher.SetFirst(candidate);
            if (matcher.RealQuickRatio() < .84 || matcher.QuickRatio() < .84) continue;
            var score = matcher.Ratio();
            if (score < .84 || score < bestScore || score == bestScore && best is not null && string.CompareOrdinal(candidate, best) <= 0) continue;
            best = candidate;
            bestScore = score;
        }
        return best;
    }
    private static HashSet<int> CandidateIds(CatalogIndex index, IEnumerable<string> tokens, string[] codes, CancellationToken token)
    {
        const int cap = 2500;
        var scored = tokens.Select(t => (Frequency: DocumentFrequency(index, t), Term: t)).Where(p => p.Frequency > 0)
            .OrderBy(p => p.Frequency).ThenBy(p => p.Term, StringComparer.Ordinal).ToArray();
        var codeFirst = codes.Where(c => DocumentFrequency(index, c) > 0).ToArray();
        var ordered = codeFirst.Concat(scored.Select(p => p.Term).Where(t => !codeFirst.Contains(t, StringComparer.Ordinal)));
        var result = new HashSet<int>();
        foreach (var term in ordered)
        {
            token.ThrowIfCancellationRequested();
            var ids = index.Postings[term];
            if (ids.Count > 60000) continue;
            result.UnionWith(ids);
            if (result.Count >= cap) break;
        }
        if (result.Count == 0)
            foreach (var (_, term) in scored.Take(2))
            {
                token.ThrowIfCancellationRequested();
                result.UnionWith(index.Postings[term].Take(cap));
                if (result.Count >= cap) break;
            }
        return result;
    }
}
