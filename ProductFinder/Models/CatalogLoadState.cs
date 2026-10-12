namespace ProductFinder.Models;

public sealed record CatalogLoadProgress(int? Percent, string Message);
public sealed record CatalogLoadResult(CatalogIndex Index, bool FromCache, string? CacheWarning);
