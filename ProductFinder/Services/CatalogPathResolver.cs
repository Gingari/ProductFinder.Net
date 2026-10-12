using System;
using System.IO;

namespace ProductFinder.Services;

public static class CatalogPathResolver
{
    public static string Resolve(string? baseDirectory = null)
    {
        var folder = baseDirectory ?? AppContext.BaseDirectory;
        var resourcePath = Path.Combine(folder, "Resources", "products.db");
        if (File.Exists(resourcePath)) return resourcePath;
        var rootPath = Path.Combine(folder, "products.db");
        return File.Exists(rootPath) ? rootPath : resourcePath;
    }
}
