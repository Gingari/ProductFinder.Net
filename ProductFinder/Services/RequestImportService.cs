using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProductFinder.Models;

namespace ProductFinder.Services;

public sealed class RequestImportService(IProductSearchService search)
{
    public Task<RequestImportResult> PrepareTableAsync(CatalogIndex index, IReadOnlyList<RequestLine> lines,
        IProgress<WorkflowProgress>? progress, CancellationToken token) => Task.Run(() =>
    {
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var merged = new List<RequestLine>();
        foreach (var line in lines)
        {
            token.ThrowIfCancellationRequested();
            if (line.Quantity <= 0) throw new InvalidDataException("Количество должно быть положительным: " + line.Article);
            var key = line.Article.Length > 0 ? CatalogText.Normalize(line.Article).Replace(" ", "") : CatalogText.Normalize(line.Name);
            if (positions.TryGetValue(key, out var at)) merged[at] = merged[at] with { Quantity = checked(merged[at].Quantity + line.Quantity) };
            else { positions[key] = merged.Count; merged.Add(line); }
        }
        var items = new List<ExportItem>();
        var missing = new List<string>();
        var found = 0;
        var lastPercent = -1;
        for (var i = 0; i < merged.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var line = merged[i];
            var source = (line.Article + " " + line.Name).Trim();
            var id = line.Article.Length > 0 ? search.LookupArticle(index, line.Article, true) : null;
            if (id is int rowId)
            {
                var product = index.Products[rowId];
                var assessment = ProductMatchGuard.Evaluate(new(rowId, product, 1) { IsExactArticle = true }, source);
                var matched = new ExportItem(product.Article, line.Quantity, product.Name, product.Manufacturer, null, source, SelectionOrigin.Article);
                matched.ApplyAssessment(assessment);
                items.Add(matched);
                found++;
            }
            else
            {
                items.Add(new(line.Article.Length > 0 ? line.Article : "НЕ УКАЗАН", line.Quantity,
                    line.Name.Length > 0 ? line.Name : "НЕТ В БАЗЕ", "", 0, source, SelectionOrigin.Unmatched));
                missing.Add(line.Article + " — " + line.Name);
            }
            var percent = (i + 1) * 100 / Math.Max(merged.Count, 1);
            if (percent == lastPercent) continue;
            progress?.Report(new(percent, $"Импорт: {i + 1}/{merged.Count}"));
            lastPercent = percent;
        }
        return new RequestImportResult(items, lines.Count, merged.Count, found, missing.Count, missing);
    }, token);

    public async Task<SmartInsertPlan> PrepareTextAsync(CatalogIndex index, SmartInsertRequest request,
        IProgress<WorkflowProgress>? progress, CancellationToken token)
    {
        var isTable = request.Mode == SmartInsertMode.PriceTable || request.Mode == SmartInsertMode.Auto && TextRequestParser.LooksLikePriceTable(request.Text);
        if (isTable)
        {
            var table = await Task.Run(() => TextRequestParser.ParsePriceTable(request.Text), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(Array.Empty<SmartPlannedRow>(), table);
        }
        var sourceItems = await Task.Run(() => TextRequestParser.SplitItems(request.Text), token).ConfigureAwait(false);
        var rows = new List<SmartPlannedRow>();
        var strict = request.Mode == SmartInsertMode.ArticlesOnly;
        var useArticles = request.Mode is SmartInsertMode.Auto or SmartInsertMode.ArticlesOnly;
        var lastPercent = -1;
        for (var i = 0; i < sourceItems.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var item = sourceItems[i];
            var tokens = useArticles ? CatalogText.ExtractArticleCandidates(item.SourceText) : Array.Empty<string>();
            int? rowId = null;
            foreach (var article in tokens) { rowId = search.LookupArticle(index, article, strict); if (rowId is not null) break; }
            if (item.Quantity <= 0)
                rows.Add(new(item.SourceText, item.Quantity, SmartRowState.Invalid, null, tokens.FirstOrDefault(), Array.Empty<SearchResult>()));
            else if (rowId is int foundId)
            {
                var candidates = strict ? Array.Empty<SearchResult>() : await search.FindCandidatesAsync(index, item.SourceText, 30, token).ConfigureAwait(false);
                var assessment = ProductMatchGuard.Evaluate(new(foundId, index.Products[foundId], 1) { IsExactArticle = true }, item.SourceText);
                if (assessment.NeedsReview && !strict)
                {
                    candidates = new[] { assessment }.Concat(candidates.Where(c => c.RowId != foundId)).ToArray();
                    rows.Add(new(item.SourceText, item.Quantity, SmartRowState.Weak, index.Products[foundId], null, candidates) { Assessment = assessment });
                }
                else rows.Add(new(item.SourceText, item.Quantity, SmartRowState.Article, index.Products[foundId], null, candidates) { Assessment = assessment });
            }
            else if (strict)
                rows.Add(new(item.SourceText, item.Quantity, SmartRowState.StrictMissing, null, tokens.FirstOrDefault(), Array.Empty<SearchResult>()));
            else
            {
                var candidates = await search.FindCandidatesAsync(index, item.SourceText, 30, token).ConfigureAwait(false);
                var score = candidates.Count > 0 ? candidates[0].Score : 0;
                var state = score >= .62 && !candidates[0].NeedsReview ? SmartRowState.Strong : score >= .34 ? SmartRowState.Weak : SmartRowState.Missing;
                rows.Add(new(item.SourceText, item.Quantity, state, null, null, candidates));
            }
            var percent = (i + 1) * 100 / Math.Max(sourceItems.Count, 1);
            if (percent != lastPercent) { progress?.Report(new(percent, $"Подбор: {i + 1}/{sourceItems.Count}")); lastPercent = percent; }
        }
        return new(rows, null);
    }
}
