using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ProductFinder.Models;

namespace ProductFinder.Services;

public static class TextRequestParser
{
    private const string GreetingPattern = @"^(здравствуйте|добрый\s+(день|вечер|утро)|доброе\s+утро|привет|заявка|спасибо|прошу|просьба|заказ|коммерческое|кп|итого|всего|счет|счёт)\b";
    private static Regex Rx(string pattern, bool ignoreCase = false) => CatalogText.GetRegex(pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
    private static int Number(string text) => int.Parse(Rx(@"\s+").Replace(text, ""), CultureInfo.InvariantCulture);
    public static string Preprocess(string? raw)
    {
        var text = Rx(@"<\s*br\s*/?\s*>", true).Replace(raw ?? "", "\n");
        text = Rx(@"<[^>]{1,20}>").Replace(text, " ");
        text = text.Replace('\u00a0', ' ').Replace("__", "").Replace("''", "\"")
            .Replace('«', '"').Replace('»', '"').Replace('“', '"').Replace('”', '"');
        text = Rx(@"(?<=[A-Za-zА-Яа-я0-9])(шт[.,]?\s)", true).Replace(text, " $1");
        text = Rx(@"(шт[.,]?)\s(\d{1,5})\s*(?=[A-ZА-Я])", true).Replace(text, "$1 $2\n");
        text = Rx(@"(шт[.,]?)\s(\d{1,5})\s*$", true).Replace(text, "$1 $2\n");
        text = Rx(@"\s+(Арт\s*[.,]?)", true).Replace(text, "\n$1");
        return Rx(@"[ \t]+").Replace(text, " ");
    }
    public static (int? Quantity, string Text) ExtractQuantity(string line)
    {
        var text = line.Trim();
        var match = Rx(@"шт[.,\s]*(\d{1,5})\b", true).Match(text);
        if (!match.Success) match = Rx(@"(\d{1,5})\s*шт[.,\s]?", true).Match(text);
        if (match.Success)
            return (Number(match.Groups[1].Value), (text[..match.Index] + " " + text[(match.Index + match.Length)..]).Trim(' ', '-', '–', '—', ':', ',', '.'));
        match = Rx(@"[-–—:]\s*(\d{1,5})\s*$").Match(text);
        if (match.Success) return (Number(match.Groups[1].Value), text[..match.Index].Trim(' ', '-', '–', '—', ':', ',', '.'));
        match = Rx(@"^(\d{1,5})\s*(.+)$").Match(text);
        if (match.Success) return (Number(match.Groups[1].Value), match.Groups[2].Value.Trim());
        match = Rx(@"^(.+?)\s+(\d{1,5})$").Match(text);
        if (match.Success) return (Number(match.Groups[2].Value), match.Groups[1].Value.Trim());
        return Rx(@"^\d{1,5}$").IsMatch(text) ? (Number(text), "") : (null, text);
    }
    public static string CleanItem(string text) => Rx(@"^\s*арт\s.?\s*", true).Replace(text, "").Trim(' ', '-', '–', '—', ':', ',', '.', ';');
    public static IReadOnlyList<TextRequestItem> SplitItems(string? raw)
    {
        var items = new List<TextRequestItem>();
        string? pending = null;
        foreach (var line in Rx(@"\r\n|[\n\r\v\f\u0085\u2028\u2029]").Split(Preprocess(raw)).Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            if (Rx(GreetingPattern, true).IsMatch(line) && CatalogText.ExtractArticleCandidates(line).Length == 0) continue;
            var (quantity, rest) = ExtractQuantity(line);
            rest = CleanItem(rest);
            if (quantity is int onlyQuantity && rest.Length == 0)
            {
                if (pending is not null) { items.Add(new(pending, onlyQuantity)); pending = null; }
                continue;
            }
            if (quantity is int number)
            {
                if (pending is not null) { items.Add(new(pending, 1)); pending = null; }
                if (rest.Length >= 3) items.Add(new(rest, number));
                continue;
            }
            if (rest.Length >= 3 && rest.Any(char.IsLetter))
            {
                if (pending is not null) items.Add(new(pending, 1));
                pending = rest;
            }
        }
        if (pending is not null) items.Add(new(pending, 1));
        return items;
    }
    public static bool LooksLikePriceTable(string? raw) => (raw ?? "").Count(c => c == '\t') >= 6 && Rx(@"Кол-?во|Кратность", true).IsMatch(raw ?? "");
    public static (string? Article, string Name) SplitArticleName(string? segment)
    {
        var text = (segment ?? "").Trim();
        if (text.Length == 0) return (null, "");
        text = Rx(@"^\d{1,4}(?=[A-Za-z])").Replace(text, "");
        var match = Rx(@"[А-Яа-яЁё]").Match(text);
        var head = (match.Success ? text[..match.Index] : text).Trim();
        var tail = match.Success ? text[match.Index..] : "";
        if (head.Length == 0) return (null, segment ?? "");
        match = Rx(@"(?<=\d)([A-Za-z]{2,})$").Match(head);
        if (match.Success) { tail = head[match.Index..] + tail; head = head[..match.Index]; }
        var article = head.Trim(' ', '-', '.', ',', ';', ':');
        return article.Length < 4 || !Rx(@"\d").IsMatch(article) || !Rx(@"[A-Za-z]").IsMatch(article)
            ? (null, segment ?? "") : (article, tail.Trim());
    }
    public static IReadOnlyList<RequestLine> ParsePriceTable(string? raw)
    {
        var text = (raw ?? "").Replace('\u00a0', ' ').Replace('\r', '\n');
        var output = new List<RequestLine>();
        if (text.Count(c => c == '\t') >= 4)
        {
            foreach (var line in text.Split('\n'))
            {
                var cells = line.Split('\t').Select(c => c.Trim()).Where(c => c.Length > 0).ToArray();
                if (cells.Length < 2 || !Rx(@"^[A-Za-z][A-Za-z0-9-./]{3,}$").IsMatch(cells[0])) continue;
                var quantity = cells.Skip(1).FirstOrDefault(c => Rx(@"^\d[\d\s]*$").IsMatch(c));
                if (quantity is null || Number(quantity) <= 0) continue;
                output.Add(new(cells[0], Rx(@"^\d[\d\s]*$").IsMatch(cells[1]) ? "" : cells[1], Number(quantity)));
            }
            if (output.Count > 0) return output;
        }
        string? currentArticle = null;
        var currentName = "";
        foreach (var segment in text.Split('\n').Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var (article, name) = SplitArticleName(segment);
            if (article is not null) { currentArticle = article; currentName = name; continue; }
            if (currentArticle is null) continue;
            var match = Rx(@"^\s*(\d[\d\s]*)").Match(segment);
            if (!match.Success) continue;
            var quantity = Number(match.Groups[1].Value);
            if (quantity > 0) output.Add(new(currentArticle, currentName, quantity));
            currentArticle = null;
        }
        return output;
    }
}
