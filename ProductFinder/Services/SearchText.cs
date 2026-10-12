using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ProductFinder.Models;

namespace ProductFinder.Services;

internal sealed record SearchDocument(string NormalizedName, HashSet<string> Tokens, Dictionary<string, string[]> Specs);

internal static class SearchText
{
    private static readonly ConditionalWeakTable<CatalogProduct, SearchDocument> Documents = new();
    private static Regex Rx(string pattern) => CatalogText.GetRegex(pattern, RegexOptions.IgnoreCase);
    internal static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "автомат", "автоматический", "выключатель", "дифференциальный", "розетка", "кабель", "провод", "светильник", "реле", "контактор", "узо", "авдт", "пускатель", "счетчик"
    };
    internal static readonly HashSet<string> NoiseWords = new(StringComparer.Ordinal)
    {
        "нужно", "нужен", "нужна", "нужны", "требуется", "требуются", "желательно", "пожалуйста", "поставить", "поставка", "найти", "подобрать", "товар", "позиция", "заявка", "количество", "кол-во", "артикул", "арт"
    };
    public static string[] ArticleCandidates(string text) => Rx(@"[\p{L}\p{N}][\p{L}\p{N}_./-]*").Matches(text)
        .Select(m => m.Value.Trim('.', '-'))
        .Where(t => t.Any(char.IsDigit) && (t.Any(char.IsLetter) || t.Length >= 5))
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(t => t.Length).ToArray();
    public static string RemoveArticles(string text, IEnumerable<string> articles)
    {
        var keys = articles.Select(a => CatalogText.Normalize(a).Replace(" ", "").Replace("-", "")).ToHashSet(StringComparer.Ordinal);
        return Rx(@"[\p{L}\p{N}][\p{L}\p{N}_./-]*").Replace(text, m =>
            keys.Contains(CatalogText.Normalize(m.Value.Trim('.', '-')).Replace(" ", "").Replace("-", "")) ? " " : m.Value);
    }
    public static string Normalize(string text)
    {
        var value = Rx(@"(?<=\d),(?=\d)").Replace(text, ".");
        value = CatalogText.Normalize(value);
        value = Rx(@"\bip\s*(\d{2})\b").Replace(value, "ip$1");
        value = Rx(@"\b(\d+(?:\.\d+)?)\s*(ма|ma)\b").Replace(value, "$1ма");
        value = Rx(@"\b(\d+(?:\.\d+)?)\s*(ка|ka|кa|kа)\b").Replace(value, "$1ка");
        value = Rx(@"\b(\d+(?:\.\d+)?)\s*[аa]\b").Replace(value, "$1а");
        value = Rx(@"\b(\d+(?:\.\d+)?)\s*[вv]\b").Replace(value, "$1в");
        value = Rx(@"\b(\d+(?:\.\d+)?)\s*(вт|w)\b").Replace(value, "$1вт");
        value = Rx(@"\b([1-4])\s*[pрп]\b").Replace(value, "$1p");
        return Rx(@"\b(\d+)\s*[xх×]\s*(\d+(?:\.\d+)?)").Replace(value, "$1x$2");
    }
    public static SearchDocument Document(CatalogProduct product) => Documents.GetValue(product, p =>
    {
        var normalized = Normalize(p.Name);
        var tokens = new HashSet<string>(CatalogText.Tokenize(Normalize(p.Name + " " + p.ManufacturerRaw.Replace('|', ' ') + " " + p.Article)), StringComparer.Ordinal);
        return new(normalized, tokens, ExtractSpecs(p.Name));
    });
    public static Dictionary<string, string[]> ExtractSpecs(string text)
    {
        var raw = Rx(@"(?<=\d),(?=\d)").Replace(text.ToLowerInvariant().Replace('ё', 'е').Replace('\u00a0', ' '), ".");
        var segments = Rx(@"\s+/\s+").Split(raw);
        if (segments.Length > 1)
        {
            var combined = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var segment in segments)
                foreach (var (key, values) in ExtractSpecs(segment))
                    combined[key] = combined.GetValueOrDefault(key, Array.Empty<string>()).Concat(values).Distinct(StringComparer.Ordinal).ToArray();
            return combined;
        }
        var normalized = CatalogText.Normalize(raw);
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string Canonical(string number) => decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n.ToString("0.########################", CultureInfo.InvariantCulture) : number;
        void Add(string key, string pattern, bool numeric = true)
        {
            var values = Rx(pattern).Matches(raw).Select(m => numeric ? Canonical(m.Groups[1].Value) : m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length > 0) result[key] = values;
        }
        Add("ip", @"\bip\s*(\d{2})\b");
        Add("leakage_ma", @"\b(\d+(?:\.\d+)?)\s*(?:ма|ma)\b");
        Add("breaking_ka", @"\b(\d+(?:\.\d+)?)\s*(?:ка|ka|кa|kа)\b");
        Add("current_a", @"(?<![кkмm\d])(?:[bcdвсд]\s*)?(\d+(?:\.\d+)?)\s*[аa]\b");
        Add("voltage_v", @"\b(\d{2,4})\s*[вv]\b");
        var poleMatches = Rx(@"\b([1-4])\s*[pрп](\s*\+\s*[nн])?\b").Matches(raw);
        if (poleMatches.Count > 0) result["poles"] = poleMatches.Select(m => m.Groups[1].Value + (m.Groups[2].Success ? "+n" : "")).Distinct().ToArray();
        else Add("poles", @"\b([1-4])\s*(?:полюс\w*|пол\.)");
        if (!result.ContainsKey("poles"))
        {
            var naturalPoles = Rx(@"\b([1-4])p\b").Matches(normalized);
            if (naturalPoles.Count > 0) result["poles"] = naturalPoles.Select(m => m.Groups[1].Value).Distinct().ToArray();
        }
        var types = new (string Type, string Pattern)[]
        {
            ("диф_автомат", @"\b(авдт|дифавтомат\w*|диф\.?\s*автомат\w*|дифференциальн\w*\s+автомат\w*)\b"),
            ("узо", @"\b(узо|вдт|дифференциальн\w*\s+выключател\w*|выключател\w*\s+дифференциальн\w*)\b"),
            ("автомат", @"\b(автомат\w*|автоматическ\w*)\b"), ("розетка", @"\bрозетк\w*\b"),
            ("выключатель", @"\bвыключател\w*\b"), ("контактор", @"\bконтактор\w*\b"),
            ("пускатель", @"\bпускател\w*\b"), ("реле", @"\bреле\b"), ("светильник", @"\bсветильник\w*\b"),
            ("кабель", @"\b(кабел\w*|провод\w*)\b"), ("счетчик", @"\bсчетчик\w*\b")
        };
        foreach (var (type, pattern) in types)
            if (Rx(pattern).IsMatch(normalized)) { result["product_type"] = new[] { type }; break; }
        var curve = Rx(@"(?:характеристика|хар(?:-?ка)?|кривая)\s*([bcdвсд])\b").Matches(raw).Select(m => m.Groups[1].Value).ToArray();
        if (curve.Length == 0) curve = Rx(@"\b([bcdвсд])\s*\d+(?:\.\d+)?\s*[аa]\b").Matches(raw).Select(m => m.Groups[1].Value).ToArray();
        if (!result.TryGetValue("product_type", out var typeValues) || typeValues[0] is "автомат" or "диф_автомат")
        {
            var shortCurve = Rx(@"\b([bcdвсд])\s*(\d+(?:\.\d+)?)(?:\s*[аa])?\b").Matches(raw);
            if (curve.Length == 0) curve = shortCurve.Select(m => m.Groups[1].Value).ToArray();
            if (!result.ContainsKey("current_a") && shortCurve.Count > 0) result["current_a"] = shortCurve.Select(m => Canonical(m.Groups[2].Value)).Distinct().ToArray();
        }
        if (curve.Length > 0) result["curve"] = curve.Select(v => v switch { "в" => "b", "с" => "c", "д" => "d", _ => v }).Distinct().ToArray();
        var mounts = new List<string>();
        if (normalized.Contains("открытый", StringComparison.Ordinal)) mounts.Add("open");
        if (normalized.Contains("скрытый", StringComparison.Ordinal)) mounts.Add("hidden");
        if (mounts.Count > 0) result["mount"] = mounts.ToArray();
        var seats = Rx(@"\b(одно|двух|трех)мест").Matches(normalized);
        if (seats.Count > 0) result["seats"] = seats.Select(m => m.Groups[1].Value switch { "одно" => "1", "двух" => "2", _ => "3" }).Distinct().ToArray();
        var section = Rx(@"\b(\d{1,2})\s*[xх×]\s*(\d+(?:\.\d+)?)").Matches(raw);
        if (section.Count > 0)
        {
            result["cores"] = section.Select(m => Canonical(m.Groups[1].Value)).Distinct().ToArray();
            result["section_mm2"] = section.Select(m => Canonical(m.Groups[2].Value)).Distinct().ToArray();
        }
        var power = Rx(@"\b(\d+(?:\.\d+)?)\s*(квт|kw|вт|w)\b").Matches(raw);
        if (power.Count > 0) result["power_w"] = power.Select(m => (decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) *
            (m.Groups[2].Value is "квт" or "kw" ? 1000 : 1)).ToString("0.################", CultureInfo.InvariantCulture)).Distinct().ToArray();
        var colors = new (string Color, string Pattern)[]
        {
            ("белый", @"\bбел(ый|ая|ое|ые|ого|ой|ую)\b"), ("черный", @"\bчерн(ый|ая|ое|ые|ого|ой|ую)\b"),
            ("серый", @"\bсер(ый|ая|ое|ые|ого|ой|ую)\b"), ("красный", @"\bкрасн(ый|ая|ое|ые|ого|ой|ую)\b"),
            ("синий", @"\bсин(ий|яя|ее|ие|его|ей|юю)\b"), ("зеленый", @"\bзелен(ый|ая|ое|ые|ого|ой|ую)\b"),
            ("желтый", @"\bжелт(ый|ая|ое|ые|ого|ой|ую)\b")
        };
        var foundColors = colors.Where(c => Rx(c.Pattern).IsMatch(raw)).Select(c => c.Color).ToArray();
        if (foundColors.Length > 0) result["color"] = foundColors;
        return result;
    }
    public static bool IsSpecToken(string token) => Rx(@"^(?:ip\d{2}|[1-4][pрп]|[bcdвсд]\d+(?:\.\d+)?(?:[аa])?|\d+(?:\.\d+)?(?:ма|ma|ка|ka|[аaвv]|вт|w|квт|kw|мм(?:2|²)?|м)|\d+[xх×]\d+(?:\.\d+)?)$").IsMatch(token);
    public static IEnumerable<string> RetrievalVariants(string term)
    {
        yield return term;
        if (Rx(@"^[bcdвсд]\d+(?:\.\d+)?[аa]?$").IsMatch(term))
        {
            var letter = term[0] switch { 'в' => 'b', 'с' => 'c', 'д' => 'd', _ => term[0] };
            var number = term[1..].TrimEnd('а', 'a');
            yield return letter + number + "а";
            yield return letter + number + "a";
            yield return letter + number;
        }
        if (Rx(@"^ip\d{2}$").IsMatch(term)) { yield return "ip"; yield return term[2..]; }
        if (Rx(@"\dа$").IsMatch(term)) yield return term[..^1] + "a";
        if (Rx(@"\dв$").IsMatch(term)) yield return term[..^1] + "v";
        if (term.EndsWith("ма", StringComparison.Ordinal)) yield return term[..^2] + "ma";
        if (term.EndsWith("ка", StringComparison.Ordinal)) yield return term[..^2] + "ka";
        if (term.EndsWith("вт", StringComparison.Ordinal)) yield return term[..^2] + "w";
        if (Rx(@"^[1-4]p$").IsMatch(term)) { yield return term[..^1] + "р"; yield return term[..^1] + "п"; }
        if (term.Contains('x')) { yield return term.Replace('x', 'х'); yield return term.Replace('x', '×'); }
        if (term.Contains('.')) foreach (var part in term.Split('.')) if (part.Length > 1) yield return part;
    }
}
