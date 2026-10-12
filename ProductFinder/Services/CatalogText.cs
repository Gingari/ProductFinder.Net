using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;

namespace ProductFinder.Services;
public static class CatalogText
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "для", "или", "как", "есть", "шт", "штук", "упак", "уп", "компл", "набор",
        "серия", "серии", "цвет", "цвета", "тип", "типа", "мм", "см", "мет", "метр",
        "новый", "новая", "под", "без", "при", "все", "изд", "арт", "артикул",
        "откр", "уст", "устан", "установки", "прочее", "разное", "мод", "модель",
        "шк", "ral", "рал", "счет", "прошу", "выставить"
    };
    private static readonly (string Pattern, string Replacement)[] Stems =
    {
        (@"\bодномест\w*", "одномест"), (@"\bдвухмест\w*", "двухмест"),
        (@"\bтрехмест\w*", "трехмест"), (@"\bзаземл\w*", "заземление"),
        (@"\bоткрыт\w*", "открытый"), (@"\bскрыт\w*", "скрытый"),
        (@"\bустанов\w*", "установка"), (@"\bрозетк\w*", "розетка"),
        (@"\bвыключател\w*", "выключатель"), (@"\bавтоматическ\w*", "автоматический"),
        (@"\bдифференциальн\w*", "дифференциальный"), (@"\bоднополюс\w*", "1p"),
        (@"\bдвухполюс\w*", "2p"), (@"\bтрехполюс\w*", "3p"), (@"\bчетырехполюс\w*", "4p")
    };
    private static readonly (string Name, string[] Markers)[] ProductTypes =
    {
        ("диф_автомат", new[] { "дифференциальный автомат", "авдт" }),
        ("автомат", new[] { "автоматический выключатель", "автомат" }),
        ("розетка", new[] { "розетка" }), ("выключатель", new[] { "выключатель" }),
        ("контактор", new[] { "контактор" }), ("реле", new[] { "реле" }),
        ("светильник", new[] { "светильник" }), ("кабель", new[] { "кабель", "провод" })
    };
    private static readonly string[] ManufacturerPriority = { "IEK", "EKF" };
    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Flags), Regex> Regexes = new();
    internal static Regex GetRegex(string pattern, RegexOptions flags = RegexOptions.None) =>
        Regexes.GetOrAdd((pattern, flags), key => new Regex(key.Pattern,
            key.Flags | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(2)));
    private static string Replace(string text, string pattern, string replacement) =>
        GetRegex(pattern).Replace(text, replacement);

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text.ToLowerInvariant().Replace('ё', 'е');
        s = Replace(s, @"\bз\s*/\s*к\b", " заземление ");
        s = Replace(s, @"\bотк\.?\s*уст\.?\b", " открытая установка ");
        s = Replace(s, @"\bскр\.?\s*уст\.?\b", " скрытая установка ");
        s = Replace(s, @"\bавт\.?\s*выкл\.?\b", " автоматический выключатель ");
        s = Replace(s, @"\bдиф\.?\s*автомат\b", " дифференциальный автомат ");
        foreach (var rule in Stems) s = Replace(s, rule.Pattern, rule.Replacement);
        s = Replace(s, @"\bшк\b.?\s\d+\s", " ");
        s = s.Replace('–', '-').Replace('—', '-').Replace('−', '-').Replace('‑', '-');
        s = Replace(s, @"[\(\)\[\]\{\},;:]", " ");
        s = Replace(s, @"(?<=[^\d])\.", " ");
        s = Replace(s, @"(?<=\d)\s[xх×]\s(?=\d)", "x");
        s = Replace(s, @"\bip\s.?\s(\d{2})\b", "ip$1");
        s = Replace(s, @"(?<=\d)\s+(вт|а|в|квт|ва|ка|гц|м|мм)\b", "$1");
        s = s.Replace('+', ' ').Replace('/', ' ');
        s = Replace(s, @"\s-\s", "-");
        return Replace(s, @"\s+", " ").Trim();
    }

    public static string[] Tokenize(string normalized)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in normalized.Split(' '))
        {
            var token = raw.Trim('-');
            if (token.Length >= 2 && !Stopwords.Contains(token)) result.Add(token);
            if (!token.Contains('-')) continue;
            foreach (var part in token.Split('-'))
                if (part.Length >= 2 && !Stopwords.Contains(part)) result.Add(part);
        }
        return result.ToArray();
    }

    public static Dictionary<string, string[]> ExtractSpecs(string text)
    {
        var n = Normalize(text);
        var specs = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string[] Values(string pattern, bool decimalValue = false) =>
            GetRegex(pattern, RegexOptions.IgnoreCase).Matches(n)
                .Select(m => decimalValue ? m.Groups[1].Value.Replace(',', '.') : m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal).ToArray();
        void Add(string key, string pattern, bool decimalValue = false)
        {
            var values = Values(pattern, decimalValue);
            if (values.Length > 0) specs[key] = values;
        }
        Add("ip", @"\bip\s*(\d{2})\b");
        Add("leakage_ma", @"\b(\d+(?:[.,]\d+)?)\s*(?:ма|ma)\b", true);
        Add("breaking_ka", @"\b(\d+(?:[.,]\d+)?)\s*(?:ка|ka|кa|kа)\b", true);
        Add("current_a", @"(?<![кkмm\d])(?:[bcdвсд]\s*)?(\d+(?:[.,]\d+)?)\s*[аa]\b", true);
        Add("voltage_v", @"\b(\d{2,4})\s*[вv]\b");
        Add("poles", @"\b([1-4])\s*[pрп](?:\s*\+\s*[nн])?\b");
        var curve = Values(@"(?:характеристика|хар(?:-?ка)?|кривая)\s*([bcdвсд])\b");
        if (curve.Length == 0) curve = Values(@"\b([bcdвсд])\s*\d+(?:[.,]\d+)?\s*[аa]\b");
        if (curve.Length > 0)
            specs["curve"] = curve.Select(v => v switch { "в" => "b", "с" => "c", "д" => "d", _ => v })
                .Distinct(StringComparer.Ordinal).ToArray();
        foreach (var type in ProductTypes)
        {
            if (!type.Markers.Any(marker => n.Contains(marker, StringComparison.Ordinal))) continue;
            specs["product_type"] = new[] { type.Name };
            break;
        }
        if (n.Contains("открытый", StringComparison.Ordinal)) specs["mount"] = new[] { "open" };
        else if (n.Contains("скрытый", StringComparison.Ordinal)) specs["mount"] = new[] { "hidden" };
        return specs;
    }

    public static string[] ExtractCodes(string normalized)
    {
        return normalized.Split(' ').Select(t => t.Trim('-'))
            .Where(t => t.Length >= 4 && t.Any(char.IsDigit) && (t.Any(char.IsLetter) || t.Contains('-')))
            .Where(t => !Stopwords.Contains(t) && !GetRegex(@"^ip\d{2}$").IsMatch(t) &&
                !GetRegex(@"^\d+(вт|а|в|квт|ва|ка|гц|мм|м)$").IsMatch(t))
            .Distinct(StringComparer.Ordinal).OrderByDescending(t => t.Count(c => c == '-'))
            .ThenByDescending(t => t.Length).ThenBy(t => t, StringComparer.Ordinal).ToArray();
    }

    public static string[] ExtractArticleCandidates(string? text)
    {
        return GetRegex(@"[A-Za-zА-Яа-яЁё0-9][A-Za-zА-Яа-яЁё0-9-]{3,25}")
            .Matches(text ?? "").Select(m => m.Value.Trim('-'))
            .Where(t => t.Length >= 5 && t.Any(char.IsLetter) && t.Any(char.IsDigit) &&
                !GetRegex(@"^(ip\d{1,2}|led|din|ral\d*|\d+)$", RegexOptions.IgnoreCase).IsMatch(t))
            .Distinct(StringComparer.Ordinal).OrderByDescending(t => t.Count(c => c == '-'))
            .ThenByDescending(t => t.Length).ToArray();
    }

    public static int ManufacturerRank(string? manufacturer)
    {
        for (var i = 0; i < ManufacturerPriority.Length; i++)
            if ((manufacturer ?? "").Contains(ManufacturerPriority[i], StringComparison.OrdinalIgnoreCase)) return i;
        return ManufacturerPriority.Length;
    }

    public static string DisplayManufacturer(string? manufacturer)
    {
        var raw = (manufacturer ?? "").Trim();
        foreach (var brand in ManufacturerPriority)
            foreach (var part in raw.Replace(';', '|').Split('|'))
                if (part.Contains(brand, StringComparison.OrdinalIgnoreCase)) return part.Trim();
        return raw;
    }
}
