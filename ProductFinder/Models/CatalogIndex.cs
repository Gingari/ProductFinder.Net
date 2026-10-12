using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ProductFinder.Services;

namespace ProductFinder.Models;

public sealed class CatalogIndex
{
    public IReadOnlyList<CatalogProduct> Products { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<int>> Postings { get; }
    public IReadOnlyDictionary<string, int> ByArticle { get; }
    public IReadOnlyDictionary<string, int> ByArticleNoDash { get; }
    public IReadOnlyList<string> Vocabulary { get; }
    public bool HasManufacturer { get; }
    public int Count => Products.Count;

    internal CatalogIndex(CatalogIndexData data)
    {
        Products = data.Products.AsReadOnly();
        ByArticle = new ReadOnlyDictionary<string, int>(data.ByArticle);
        ByArticleNoDash = new ReadOnlyDictionary<string, int>(data.ByArticleNoDash);
        Vocabulary = data.Vocabulary.AsReadOnly();
        HasManufacturer = data.HasManufacturer;
        var postings = new Dictionary<string, IReadOnlyList<int>>(data.Postings.Count, StringComparer.Ordinal);
        foreach (var entry in data.Postings) postings[entry.Key] = entry.Value.AsReadOnly();
        Postings = new ReadOnlyDictionary<string, IReadOnlyList<int>>(postings);
    }
    public bool TryLookupArticle(string? article, out CatalogProduct? product)
    {
        var key = CatalogText.Normalize(article).Replace(" ", "");
        product = null;
        if (key.Length < 5) return false;
        if (!ByArticle.TryGetValue(key, out var rowId) &&
            !ByArticleNoDash.TryGetValue(key.Replace("-", ""), out rowId)) return false;
        product = Products[rowId];
        return true;
    }
}
