using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using ProductFinder.Services;

namespace ProductFinder.Models;

public sealed class CatalogProduct
{
    public string Article { get; init; } = "";
    public string Name { get; init; } = "";
    public string ManufacturerRaw { get; init; } = "";
    public string NormalizedName { get; init; } = "";
    public string ArticleKey { get; init; } = "";
    public string[] SearchTokens { get; init; } = Array.Empty<string>();
    public Dictionary<string, string[]> Specs { get; init; } = new(StringComparer.Ordinal);
    [JsonIgnore] public string Manufacturer => CatalogText.DisplayManufacturer(ManufacturerRaw);
}
