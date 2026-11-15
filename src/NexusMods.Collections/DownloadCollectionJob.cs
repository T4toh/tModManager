using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Jobs;

namespace NexusMods.Collections;

public class DownloadCollectionJob : IJobDefinitionWithStart<DownloadCollectionJob, R3.Unit>
{
    public required ILogger<DownloadCollectionJob> Logger { get; init; }
    public required CollectionRevisionMetadata.ReadOnly RevisionMetadata { get; init; }
    public required CollectionDownloader.ItemType ItemType { get; init; }
    public required CollectionDownloader Downloader { get; init; }
    public required IDb Db { get; init; }
    public required int MaxDegreeOfParallelism { get; init; }

    public async ValueTask<R3.Unit> StartAsync(IJobContext<DownloadCollectionJob> context)
    {
        var downloads = RevisionMetadata.Downloads.ToArray();

        // Without a Nexus website session every file fails the same way: stop at the first one and tell the caller
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        NexusWebsiteSessionRequiredException? noSession = null;

        try
        {
            await Parallel.ForAsync(fromInclusive: 0, toExclusive: downloads.Length, parallelOptions: new ParallelOptions
            {
                CancellationToken = stop.Token,
                MaxDegreeOfParallelism = MaxDegreeOfParallelism == -1 ? Environment.ProcessorCount : MaxDegreeOfParallelism,
            }, body: async (index, token) =>
            {
                var download = downloads[index];
                if (!CollectionDownloader.DownloadMatchesItemType(download, ItemType)) return;
                if (CollectionDownloader.GetStatus(download, Db).IsDownloaded())
                {
                    // Verify the archive physically exists — if it was deleted, re-download
                    var validatedStatus = await Downloader.ValidateStatusAsync(CollectionDownloader.GetStatus(download, Db));
                    if (validatedStatus.IsDownloaded()) return;
                }

                try
                {
                    if (download.TryGetAsCollectionDownloadNexusMods(out var nexusModsDownload))
                    {
                        await Downloader.Download(nexusModsDownload, token, openBrowserOnFailure: false);
                    }
                    else if (download.TryGetAsCollectionDownloadExternal(out var externalDownload))
                    {
                        await Downloader.Download(externalDownload, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // ignored
                }
                catch (NexusWebsiteSessionRequiredException e)
                {
                    noSession = e;
                    Logger.LogWarning("Sin sesión de nexusmods.com en Firefox: se corta la descarga de `{CollectionName}/{RevisionNumber}`", download.CollectionRevision.Collection.Slug, download.CollectionRevision.RevisionNumber);
                    await stop.CancelAsync();
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Exception while downloading `{DownloadName}` from `{CollectionName}/{RevisionNumber}`", download.Name, download.CollectionRevision.Collection.Slug, download.CollectionRevision.RevisionNumber);
                }
            });
        }
        catch (OperationCanceledException) when (noSession is not null)
        {
            // stopped by us, reported below
        }

        if (noSession is not null) throw noSession;
        return R3.Unit.Default;
    }
}
