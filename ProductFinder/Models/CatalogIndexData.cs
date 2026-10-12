using System;
using System.Collections.Generic;

namespace ProductFinder.Models;

internal sealed class CatalogIndexData
{
    public CatalogIndexData() { }
    public List<CatalogProduct> Products { get; set; } = new();
    public Dictionary<string, List<int>> Postings { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ByArticle { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> ByArticleNoDash { get; set; } = new(StringComparer.Ordinal);
    public List<string> Vocabulary { get; set; } = new();
    public bool HasManufacturer { get; set; }
}
