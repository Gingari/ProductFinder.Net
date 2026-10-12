using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ProductFinder.Models;

namespace ProductFinder.Services;

public interface IProductSearchService
{
    int? LookupArticle(CatalogIndex index, string article, bool strict = false);
    Task<IReadOnlyList<SearchResult>> SearchAsync(CatalogIndex index, string query, int limit, CancellationToken token);
    Task<IReadOnlyList<SearchResult>> FindCandidatesAsync(CatalogIndex index, string query, int limit, CancellationToken token);
}
