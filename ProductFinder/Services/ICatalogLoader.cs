using System;
using System.Threading;
using System.Threading.Tasks;
using ProductFinder.Models;

namespace ProductFinder.Services;

public interface ICatalogLoader
{
    Task<CatalogLoadResult> LoadAsync(string databasePath, IProgress<CatalogLoadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
