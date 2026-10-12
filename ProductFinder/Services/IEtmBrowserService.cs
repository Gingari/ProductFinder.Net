using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ProductFinder.Models;

namespace ProductFinder.Services;

public interface IEtmBrowserService : IAsyncDisposable
{
    Task<IReadOnlyList<EtmCandidate>> SearchAsync(string query, IProgress<string>? progress, CancellationToken token);
    Task<EtmProduct> ReadProductAsync(EtmCandidate candidate, IProgress<string>? progress, CancellationToken token);
    Task ShowBrowserAsync(CancellationToken token);
}
public interface IEtmDialogService
{
    EtmSaveResult? ParseEtm(string sourceText, string databasePath);
}

public interface IEtmBrowserModeService
{
    bool RunsInBackground { get; }
    Task SetBackgroundAsync(bool background, CancellationToken token);
}
