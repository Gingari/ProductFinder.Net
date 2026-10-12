using ProductFinder.Models;
using ProductFinder.Services;

internal static class SearchQualityTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception("Quality: " + message); }
    internal static CatalogIndex Make(params (string Article, string Name, string Brand)[] rows)
    {
        var data = new CatalogIndexData { HasManufacturer = true };
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var key = CatalogText.Normalize(row.Article).Replace(" ", "");
            var tokens = CatalogText.Tokenize(CatalogText.Normalize(row.Name + " " + row.Brand));
            data.Products.Add(new() { Article = row.Article, Name = row.Name, ManufacturerRaw = row.Brand,
                ArticleKey = key, NormalizedName = CatalogText.Normalize(row.Name), SearchTokens = tokens, Specs = CatalogText.ExtractSpecs(row.Name) });
            data.ByArticle.TryAdd(key, i); data.ByArticleNoDash.TryAdd(key.Replace("-", ""), i);
            foreach (var term in tokens)
            {
                if (!data.Postings.TryGetValue(term, out var ids)) data.Postings[term] = ids = new();
                ids.Add(i);
            }
        }
        data.Vocabulary = data.Postings.Keys.Order().ToList();
        return new(data);
    }
    internal static async Task Run(CatalogIndex fixture, ProductSearchService search)
    {
        var cases = new[]
        {
            ("автомат 1P C16А 6кА 230В", "ABC-12345", "ABC-32321"),
            ("автомат 2P C16А 6кА 230В", "ABC-54321", "ABC-12345"),
            ("кабель ВВГнг 3х2,5", "CBL-00001", "CBL-00002"),
            ("светильник 12Вт 220В IP65", "LMP-00001", "LMP-00002"),
            ("диф автомат 25А 30мА 2P", "RCD-00001", "RCD-00002"),
            ("розетка двухместная скрытая IP20", "SCK-00002", "SCK-00001")
        };
        foreach (var (query, correct, wrong) in cases)
        {
            var results = await search.SearchAsync(fixture, query, 100, default);
            Check(results[0].Article == correct, query + ": expected " + correct + ", got " + results[0].Article + "/" + results[0].Score + " " + results[0].Explanation);
            var conflict = results.Single(r => r.Article == wrong);
            Check(conflict.Score < .5 && conflict.Confidence == ConfidenceState.Missing && conflict.HasSpecConflict && !conflict.CanAutoSelect, "Conflicting specs: " + query);
            Check(results[0].Score >= .8 && results[0].CanAutoSelect, "Valid specs green: " + query + "/" + results[0].Score + " " + results[0].Explanation);
        }
        var quality = Make(
            ("IEK-00001", "Автомат 1P C32А 6кА 230В", "IEK"),
            ("EKF-00001", "Автомат 1P C16А 6кА 230В", "EKF"),
            ("EKF-00002", "Автомат 3P C16А 6кА 230В", "EKF"),
            ("UNKNOWN-01", "Автомат 1P C16А 230В", "EKF"),
            ("RCD-00003", "УЗО 2P 25А 30мА 230В", "ABB"),
            ("DIF-00003", "Дифференциальный автомат 2P C25А 30мА 230В", "IEK"),
            ("SOCKET-01", "Розетка белая скрытая IP44", "IEK"),
            ("SOCKET-02", "Розетка черная открытая IP44", "EKF"),
            ("DECIMAL-1", "Автомат 1P C16А 4,5кА 230В", "EKF"),
            ("DECIMAL-2", "Автомат 1P C16А 4кА 230В", "IEK"),
            ("NEUTRAL-1", "Автомат 1P+N C16А 6кА 230В", "IEK"),
            ("DUP-00001", "Розетка скрытая IP44", "IEK"),
            ("DUP-00001", "Розетка открытая IP44", "EKF"),
            ("001234", "Розетка", "IEK"));
        var ranked = await search.SearchAsync(quality, "автомат 1P C16А 6кА 230В", 100, default);
        Check(ranked[0].Article == "EKF-00001" && ranked[0].CanAutoSelect, "Brand priority never beats correct specs");
        var requestedBrand = await search.SearchAsync(quality, "автомат IEK 1P C16А 6кА 230В", 100, default);
        var ekf = requestedBrand.Single(r => r.Article == "EKF-00001");
        Check(ekf.Score <= .79 && ekf.Confidence == ConfidenceState.Weak && !ekf.CanAutoSelect, "Requested brand mismatch");
        var missingSpec = ranked.Single(r => r.Article == "UNKNOWN-01");
        Check(missingSpec.Score <= .79 && missingSpec.NeedsReview, "Missing required spec needs review");
        foreach (var query in new[] { "Автомат", "Розетка", "автомат 16А" })
        {
            var results = await search.SearchAsync(quality, query, 30, default);
            Check(results.Count > 0 && !results[0].CanAutoSelect, "Generic or ambiguous query: " + query);
        }
        var decimalResults = await search.SearchAsync(quality, "автомат 1P C16A 4.5kA 230V", 100, default);
        Check(decimalResults[0].Article == "DECIMAL-1" && decimalResults[0].CanAutoSelect, "Decimal comma and Latin units");
        Check(decimalResults.Single(r => r.Article == "DECIMAL-2").HasSpecConflict, "4.5 != 4");
        var rcd = await search.SearchAsync(quality, "УЗО 25А 30мА 2P 230В", 100, default);
        Check(rcd[0].Article == "RCD-00003" && rcd.Single(r => r.Article == "DIF-00003").HasSpecConflict, "UZO is not differential automatic");
        var socket = await search.SearchAsync(quality, "розетка белая скрытая IP44", 100, default);
        Check(socket[0].Article == "SOCKET-01" && socket.Single(r => r.Article == "SOCKET-02").HasSpecConflict, "Color and mount conflicts");
        foreach (var falseSku in new[] { "000ABC-12345", "ABC-12345xx", "ABC-123456789012345678901234567890" })
        {
            Check(search.LookupArticle(fixture, falseSku) is null, "Never trim article: " + falseSku);
            var result = await search.SearchAsync(fixture, falseSku, 30, default);
            Check(result.All(r => !r.IsExactArticle && !r.CanAutoSelect), "Never invent exact article: " + falseSku);
        }
        var exact = await search.SearchAsync(fixture, "abc12345", 30, default);
        Check(exact[0].IsExactArticle && exact[0].Score == 1 && exact[0].CanAutoSelect, "Exact no-dash article");
        var conflictExact = await search.SearchAsync(fixture, "ABC-12345 автомат 32А", 100, default);
        var unsafeExact = conflictExact.Single(r => r.Article == "ABC-12345");
        Check(unsafeExact.IsExactArticle && unsafeExact.Score < .5 && unsafeExact.HasSpecConflict, "Exact SKU never overrides spec conflict");
        Check(conflictExact.All(r => !r.CanAutoSelect), "Alternatives to explicit SKU require confirmation");
        var duplicate = await search.SearchAsync(quality, "DUP-00001", 30, default);
        Check(duplicate.Count(r => r.Article == "DUP-00001") == 2 && duplicate.All(r => !r.CanAutoSelect), "Duplicate SKU with different mount");
        var quantityParser = await new RequestImportService(search).PrepareTextAsync(quality,
            new("001234 2 шт", SmartInsertMode.ArticlesOnly, false, false), null, default);
        Check(quantityParser.Rows.Single().State == SmartRowState.Article && quantityParser.Rows[0].ExactProduct?.Article == "001234", "Numeric SKU in text");
        var strict = await new RequestImportService(search).PrepareTableAsync(fixture,
            new[] { new RequestLine("ABC-12345", "Автомат 32А", 2) }, null, default);
        Check(strict.FoundCount == 1 && strict.Items[0].Confidence == ConfidenceState.Missing && strict.Items[0].MatchExplanation.Contains("Противоречия"), "Excel exact SKU mismatch stays red");
        var auto = await new RequestImportService(search).PrepareTextAsync(fixture,
            new("ABC-12345 автомат 32А 2 шт", SmartInsertMode.Auto, true, false), null, default);
        Check(auto.Rows.Single().State == SmartRowState.Weak, "Smart exact conflict never auto-selects");
        var item = new ExportItem("", 2, "", "", 0, "исходник", SelectionOrigin.Search);
        item.SelectAlternative(unsafeExact);
        Check(item.Confidence == ConfidenceState.Missing && item.Quantity == 2 && item.SourceText == "исходник", "Manual alternative retains risk");
        Check(MatchQuality.Classify(.4999) == ConfidenceState.Missing && MatchQuality.Classify(.5) == ConfidenceState.Weak &&
            MatchQuality.Classify(.7999) == ConfidenceState.Weak && MatchQuality.Classify(.8) == ConfidenceState.Confirmed && MatchQuality.Percent(.7999) == "79%", "Threshold boundaries");
        var unknownTerms = await search.SearchAsync(fixture, "Автомат 16А несуществующаясерия", 100, default);
        Check(unknownTerms.All(r => !r.CanAutoSelect), "Unknown query tokens never ignored");
        var fuzzy = await search.SearchAsync(fixture, "розеткка", 30, default);
        Check(fuzzy.Count > 0 && fuzzy.All(r => !r.CanAutoSelect), "Fuzzy spelling stays conservative");
        var noMatch = await search.SearchAsync(fixture, "несуществующийтовар", 30, default);
        Check(noMatch.Count == 0 && (await search.SearchAsync(fixture, "", 30, default)).Count == 0, "Empty/unknown searches");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        try { await search.SearchAsync(fixture, "Автомат", 30, cts.Token); throw new Exception("No cancellation"); } catch (OperationCanceledException) { }
        var shortCurve = await search.SearchAsync(quality, "С16", 100, default);
        Check(shortCurve.Any(r => r.Article == "EKF-00001" && r.Score >= .5) && shortCurve.All(r => !r.CanAutoSelect), "Short Cyrillic C16 search with ambiguity guard");
        var spacedIp = await search.SearchAsync(fixture, "светильник 12 Вт 220 V IP 65", 100, default);
        Check(spacedIp[0].Article == "LMP-00001" && spacedIp[0].CanAutoSelect, "Spaced IP and Latin voltage");
        Check((await search.FindCandidatesAsync(fixture, "ABC-12345", 30, default)).All(r => !r.IsExactArticle && !r.CanAutoSelect), "Names-only search never promotes exact SKU");
        var stableSource = await search.SearchAsync(fixture, "Автомат 1P C16А 6кА 230В", 30, default);
        var snapshot = new[] { stableSource[0], missingSpec, unsafeExact }.Select((r, i) => new { r.Article, r.Name, r.Manufacturer, r.Score, r.MatchText, r.MatchLabel, r.MatchExplanation, Confidence = r.Confidence.ToString(), Selected = i == 2 }).ToArray();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ranking-preview.json"), System.Text.Json.JsonSerializer.Serialize(snapshot));
        var suffixSku = Make(("ABC-00016A", "Автомат 32А", "IEK"), ("001234", "Розетка", "IEK"));
        Check((await search.SearchAsync(suffixSku, "ABC-00016A", 30, default))[0].Score == 1, "Never parse SKU suffix as current");
        var suffixConflict = (await search.SearchAsync(suffixSku, "ABC-00016A автомат 16А", 30, default)).Single();
        Check(suffixConflict.HasSpecConflict, "SKU removed but descriptive current checked");
        var mergedConflict = await new RequestImportService(search).PrepareTableAsync(fixture,
            new[] { new RequestLine("ABC-12345", "Автомат 16А", 2), new RequestLine("ABC-12345", "Автомат 32А", 3) }, null, default);
        Check(mergedConflict.Items.Single().Confidence == ConfidenceState.Missing && mergedConflict.Items[0].Quantity == 5, "Merged duplicates preserve conflicting descriptions");
        var distinctTypes = await new RequestImportService(search).PrepareTableAsync(fixture,
            new[] { new RequestLine("ABC-12345", "Автомат 16А", 2), new RequestLine("ABC-12345", "Розетка 16А", 3) }, null, default);
        Check(distinctTypes.Items.Single().Confidence == ConfidenceState.Missing, "Merged duplicate types not ignored");
        var reorderedDup = Make(("DUP-00009", "Розетка открытая IP44", "IEK"), ("DUP-00009", "Розетка скрытая IP44", "EKF"));
        var dupTable = await new RequestImportService(search).PrepareTableAsync(reorderedDup,
            new[] { new RequestLine("DUP-00009", "Розетка скрытая IP44", 2) }, null, default);
        Check(dupTable.Items.Single().Name.Contains("скрытая") && dupTable.Items[0].Confidence == ConfidenceState.Confirmed, "Duplicate SKU resolved by description, not DB order");
        var dupPlan = await new RequestImportService(search).PrepareTextAsync(reorderedDup,
            new("DUP-00009 Розетка скрытая IP44 2 шт", SmartInsertMode.Auto, true, false), null, default);
        Check(dupPlan.Rows.Single().State == SmartRowState.Article && dupPlan.Rows[0].ExactProduct!.Name.Contains("скрытая"), "Text duplicate SKU resolved by description");
        var exactDuplicate = ProductSearchService.AssessExact(quality, search.LookupArticle(quality, "DUP-00001")!.Value, "DUP-00001");
        Check(!exactDuplicate.CanAutoSelect && exactDuplicate.Confidence == ConfidenceState.Weak, "Manual duplicate SKU not green");
        using (var vm = new ProductFinder.ViewModels.MainWindowViewModel(new FakeLoader(fixture), search, new FakeDialogs()))
        {
            await vm.InitializeAsync();
            vm.SearchQuery = "другой запрос";
            vm.AddSearchResult(unsafeExact);
            var added = vm.ExportItems.Single();
            Check(added.Confidence == ConfidenceState.Missing && added.Score == unsafeExact.Score && added.SourceText == unsafeExact.SourceQuery, "Search add retains actual score and original query");
        }
        var benchmark = Make(Enumerable.Range(0, 10000).Select(i => ($"BENCH-{i:D5}", $"Автомат {i % 3 + 1}P C{(i % 2 == 0 ? 16 : 32)}А 6кА 230В серия{i % 20}", i % 2 == 0 ? "EKF" : "IEK")).ToArray());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var benchmarkResults = await search.SearchAsync(benchmark, "Автомат 1P C16А 6кА 230В серия0", 30, default);
        Check(benchmarkResults.Count > 0 && benchmarkResults[0].Product.Name.Contains("1P C16А") && benchmarkResults[0].CanAutoSelect, "10k candidate retrieval");
        Check(benchmarkResults.Zip(benchmarkResults.Skip(1)).All(p => p.First.Score >= p.Second.Score), "Scores stay descending");
        Console.WriteLine($"PASS 10,000-row synthetic search in {watch.ElapsedMilliseconds} ms");
        Console.WriteLine("PASS quality ranking, brands, exact SKUs, numeric SKU, decimal units, conflicts, ambiguity, colors, import and cancellation");
    }
}
