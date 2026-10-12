using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ProductFinder.Models;

namespace ProductFinder.Services;

public static class ProductMatchGuard
{
    private sealed record Facts(string Key, Dictionary<string, HashSet<string>> Models, HashSet<string> Brands, Dictionary<string, string[]> Specs);
    private static readonly ConcurrentDictionary<string, Facts> Queries = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<CatalogProduct, Facts> Documents = new();
    private static readonly Dictionary<string, HashSet<string>> NoModels = new(StringComparer.Ordinal);
    private static Facts QueryFacts(string query)
    {
        if (Queries.Count > 512) Queries.Clear();
        return Queries.GetOrAdd(query, text => new(Key(text), Models(text), BrandNames(text), Specs(text)));
    }
    private static Facts DocumentFacts(CatalogProduct product) => Documents.GetValue(product, p =>
    {
        var brands = BrandNames(p.Name);
        if (brands.Count == 0) brands = BrandNames(p.ManufacturerRaw);
        return new(Key(p.Article), Models(p.Name), brands, Specs(p.Name));
    });
    public const double ConflictCeiling = .45;
    public const double BrandCeiling = .69;
    public const double UnverifiedModelCeiling = .79;
    private static readonly Dictionary<string, string> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["дбо"] = "dbo", ["dbo"] = "dbo", ["дпо"] = "dpo", ["dpo"] = "dpo",
        ["дпб"] = "dpb", ["dpb"] = "dpb", ["дсп"] = "dsp", ["dsp"] = "dsp",
        ["лпо"] = "lpo", ["lpo"] = "lpo", ["лсп"] = "lsp", ["lsp"] = "lsp",
        ["спо"] = "spo", ["spo"] = "spo", ["нпо"] = "npo", ["npo"] = "npo",
        ["нпб"] = "npb", ["npb"] = "npb", ["эк"] = "ek", ["ek"] = "ek"
    };
    private static readonly HashSet<string> IgnoredPrefixes = new(StringComparer.OrdinalIgnoreCase)
    { "шк", "shk", "ip", "ral", "рал", "гост", "gost", "din", "iec", "iso", "led" };
    private static readonly (string Name, string Pattern)[] Brands =
    {
        ("GENERICA", @"generica|генерика"), ("IEK", @"iek|иэк"), ("EKF", @"ekf|екф"),
        ("TDM", @"tdm(?:\s+electric)?|тдм"), ("Schneider Electric", @"schneider(?:\s+electric)?|шнайдер"),
        ("ABB", @"abb|абб"), ("Siemens", @"siemens|сименс"), ("Legrand", @"legrand|легранд"),
        ("КЭАЗ", @"кэаз|keaz"), ("CHINT", @"chint|чинт"), ("DEKraft", @"dekraft|декрафт"),
        ("Космос", @"космос|kosmos"), ("Navigator", @"navigator|навигатор"), ("ERA", @"эра|era")
    };
    private static readonly Dictionary<string, string> SpecLabels = new(StringComparer.Ordinal)
    {
        ["ip"] = "IP", ["current_a"] = "ток, А", ["poles"] = "число полюсов",
        ["voltage_v"] = "напряжение, В", ["leakage_ma"] = "ток утечки, мА",
        ["breaking_ka"] = "отключающая способность, кА", ["curve"] = "характеристика",
        ["product_type"] = "тип изделия", ["mount"] = "монтаж",
        ["power_w"] = "мощность, Вт", ["temperature_k"] = "цветовая температура, К",
        ["length_mm"] = "размер, мм"
    };
    private static Regex Rx(string value) => CatalogText.GetRegex(value, RegexOptions.IgnoreCase);
    private static string Key(string text) => CatalogText.Normalize(text).Replace(" ", "").Replace("-", "");
    private static string Number(string value, decimal multiplier = 1) => decimal.TryParse(value.Replace(',', '.'), NumberStyles.Float,
        CultureInfo.InvariantCulture, out var number) ? (number * multiplier).ToString("0.################", CultureInfo.InvariantCulture) : value;
    private static string ModelCode(string value) => value.ToLowerInvariant().Replace('х', 'x').Replace('а', 'a').Replace('в', 'b')
        .Replace('с', 'c').Replace('е', 'e').Replace('к', 'k').Replace('м', 'm').Replace('н', 'h').Replace('о', 'o').Replace('р', 'p').Replace('т', 't');
    private static Dictionary<string, HashSet<string>> Models(string text, string articleToIgnore = "")
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var normalized = text.ToLowerInvariant().Replace('ё', 'е').Replace('–', '-').Replace('—', '-').Replace('‑', '-');
        foreach (Match match in Rx(@"(?<![\p{L}\p{N}])(?<family>[a-zа-я]{2,8})[\s-]*(?<code>\d{3,6}[a-zа-я]{0,4}(?:[-./][a-zа-я0-9]{1,8})*)(?![\p{L}\p{N}])").Matches(normalized))
        {
            var family = match.Groups["family"].Value;
            if (IgnoredPrefixes.Contains(family) || articleToIgnore.Length > 0 && Key(match.Value) == Key(articleToIgnore)) continue;
            family = Families.GetValueOrDefault(family, family);
            if (!result.TryGetValue(family, out var values)) result[family] = values = new(StringComparer.Ordinal);
            values.Add(ModelCode(match.Groups["code"].Value));
        }
        return result;
    }
    private static HashSet<string> BrandNames(string text) => Brands.Where(b => Rx(@"(?<![\p{L}\p{N}])(?:" + b.Pattern + @")(?![\p{L}\p{N}])").IsMatch(text))
        .Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
    private static Dictionary<string, string[]> Specs(string text)
    {
        text = Rx(@"(?<=\d),(?=\d)").Replace(text, ".");
        var result = CatalogText.ExtractSpecs(text);
        var poles = Rx(@"(?<![\p{L}\d])([1-4])\s*[pрп](\s*\+\s*[nн])?(?![\p{L}\d])").Matches(text)
            .Select(m => m.Groups[1].Value + (m.Groups[2].Success ? "+n" : "")).Distinct().ToArray();
        if (poles.Length > 0) result["poles"] = poles;
        var power = Rx(@"(?<![\p{L}\d.,])(\d+(?:[.,]\d+)?)\s*(квт|kw|вт|w)(?![\p{L}\d])").Matches(text)
            .Select(m => Number(m.Groups[1].Value, m.Groups[2].Value.StartsWith("к", StringComparison.OrdinalIgnoreCase) || m.Groups[2].Value.StartsWith("k", StringComparison.OrdinalIgnoreCase) ? 1000 : 1)).Distinct().ToArray();
        if (power.Length > 0) result["power_w"] = power;
        var temperature = Rx(@"(?<![\p{L}\d])(\d{3,5})\s*[кk](?![\p{L}\d])").Matches(text).Select(m => Number(m.Groups[1].Value)).Distinct().ToArray();
        if (temperature.Length > 0) result["temperature_k"] = temperature;
        if (!Rx(@"\d\s*[xх×]\s*\d").IsMatch(text))
        {
            var length = Rx(@"(?<![\p{L}\d.,])(\d+(?:[.,]\d+)?)\s*(мм|mm)(?![\p{L}\d])").Matches(text).Select(m => Number(m.Groups[1].Value)).Distinct().ToArray();
            if (length.Length > 0) result["length_mm"] = length;
        }
        foreach (var key in new[] { "current_a", "voltage_v", "leakage_ma", "breaking_ka" })
            if (result.TryGetValue(key, out var values)) result[key] = values.Select(v => Number(v)).Distinct().ToArray();
        return result;
    }
    public static SearchResult Evaluate(SearchResult result, string query)
    {
        var conflicts = new List<string>();
        var review = new List<string>();
        var wantedFacts = QueryFacts(query);
        var actualFacts = DocumentFacts(result.Product);
        var wantedModels = wantedFacts.Key == actualFacts.Key ? NoModels : wantedFacts.Models;
        var actualModels = actualFacts.Models;
        foreach (var (family, wanted) in wantedModels)
        {
            if (actualModels.TryGetValue(family, out var actual))
            {
                if (!wanted.Any(w => actual.Any(a => a == w || a.StartsWith(w + "-", StringComparison.Ordinal) || a.StartsWith(w + ".", StringComparison.Ordinal) || a.StartsWith(w + "/", StringComparison.Ordinal) || w.All(char.IsDigit) && a.StartsWith(w, StringComparison.Ordinal) && a[w.Length..].All(char.IsLetter)))) conflicts.Add($"Модель {family.ToUpperInvariant()}: нужно {string.Join("/", wanted)}, в каталоге {string.Join("/", actual)}.");
            }
            else if (Families.ContainsValue(family))
            {
                var otherLighting = actualModels.Keys.Where(k => Families.ContainsValue(k)).ToArray();
                if (otherLighting.Length > 0 && family != "ek" && otherLighting.Any(k => k != "ek"))
                    conflicts.Add($"Серия: нужна {family.ToUpperInvariant()}, в каталоге {string.Join("/", otherLighting).ToUpperInvariant()}.");
                else review.Add($"Модель {family.ToUpperInvariant()} {string.Join("/", wanted)} не указана в названии выбранного товара — её соответствие не подтверждено.");
            }
        }
        var wantedBrands = wantedFacts.Brands;
        var actualBrands = actualFacts.Brands;
        if (wantedBrands.Count > 0 && actualBrands.Count > 0 && !wantedBrands.Overlaps(actualBrands))
            review.Add($"Производитель/марка: нужно {string.Join("/", wantedBrands)}, в каталоге {string.Join("/", actualBrands)}.");
        else if (wantedBrands.Count > 0 && actualBrands.Count == 0 && !string.IsNullOrWhiteSpace(result.Product.ManufacturerRaw)
            && result.Product.ManufacturerRaw.Trim() is not "—" and not "-" and not "НЕТ В БАЗЕ")
            review.Add($"Производитель/марка: требуется {string.Join("/", wantedBrands)}, данные производителя «{result.Product.ManufacturerRaw}» не подтверждают эту марку.");
        var wantedSpecs = wantedFacts.Specs;
        var actualSpecs = actualFacts.Specs;
        foreach (var (key, wanted) in wantedSpecs)
            if (actualSpecs.TryGetValue(key, out var actual) && !wanted.Intersect(actual, StringComparer.Ordinal).Any())
                conflicts.Add($"{SpecLabels.GetValueOrDefault(key, key)}: нужно {string.Join("/", wanted)}, в каталоге {string.Join("/", actual)}.");
        var raw = result.OriginalScore;
        var score = raw;
        if (conflicts.Count > 0) score = Math.Min(score, ConflictCeiling);
        if (review.Any(r => r.StartsWith("Производитель/марка:", StringComparison.Ordinal))) score = Math.Min(score, BrandCeiling);
        if (review.Count > 0) score = Math.Min(score, UnverifiedModelCeiling);
        var reasons = conflicts.Concat(review).ToArray();
        var explanation = reasons.Length == 0
            ? "Явных противоречий в указанных характеристиках не обнаружено. Неуказанные характеристики не проверены. Процент — оценка поиска, не вероятность."
            : "Сходство по прежнему алгоритму: " + MatchQuality.Percent(raw) + ". Итог: " + MatchQuality.Percent(score) + ".\n" + string.Join("\n", reasons);
        return result with { Score = score, UnguardedScore = raw, SourceQuery = query, HasSpecConflict = conflicts.Count > 0, NeedsReview = reasons.Length > 0, Explanation = explanation };
    }
}
