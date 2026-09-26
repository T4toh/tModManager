using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Downloads;
using NexusMods.Abstractions.FileExtractor;
using NexusMods.Abstractions.HttpDownloads;
using NexusMods.Abstractions.Library.Jobs;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.HttpDownloader;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Paths;
using NexusMods.Sdk.FileExtractor;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;

namespace NexusMods.Library;

internal class AddDownloadJob : IJobDefinitionWithStart<AddDownloadJob, LibraryFile.ReadOnly>, IAddDownloadJob
{
    public required IJobTask<IDownloadJob, AbsolutePath> DownloadJob { get; init; }
    internal required IConnection Connection { get; set; }
    internal required IServiceProvider ServiceProvider { get; set; }

    public static IJobTask<AddDownloadJob, LibraryFile.ReadOnly> Create(IServiceProvider provider, IJobTask<IDownloadJob, AbsolutePath> downloadJob)
    {
        var monitor = provider.GetRequiredService<IJobMonitor>();
        var job = new AddDownloadJob
        {
            DownloadJob = downloadJob,
            Connection = provider.GetRequiredService<IConnection>(),
            ServiceProvider = provider,
        };
        return monitor.Begin<AddDownloadJob, LibraryFile.ReadOnly>(job);
    }

    public async ValueTask<LibraryFile.ReadOnly> StartAsync(IJobContext<AddDownloadJob> context)
    {
        await context.YieldAsync();

        // This throws if download cancelled, will be caught downstream.
        await DownloadJob;

        // Fail immediately if the download produced an empty file (silent download failure)
        var downloadedPath = DownloadJob.Result;
        if (downloadedPath.FileExists && downloadedPath.FileInfo.Size == Size.Zero)
            throw new InvalidOperationException($"La descarga del archivo '{downloadedPath.FileName}' resultó en un archivo vacío (0 bytes). Es posible que la URL haya expirado o el servidor haya rechazado la petición.");

        // Preserve a copy of the original downloaded file, importing from that copy so
        // AddLibraryFileJob records LibraryFile.DownloadPath for it.
        var preserved = await PreserveOriginalFile(context.CancellationToken);

        await context.YieldAsync();
        using var tx = Connection.BeginTransaction();

        var libraryFile = await AddLibraryFileJob.Create(ServiceProvider, tx, preserved ?? DownloadJob.Result);
        await DownloadJob.JobDefinition.AddMetadata(tx, libraryFile);

        var transactionResult = await tx.Commit();
        return transactionResult.Remap(libraryFile);
    }

    private async Task<AbsolutePath?> PreserveOriginalFile(CancellationToken ct)
    {
        try
        {
            var downloadedFilePath = DownloadJob.Result;
            if (!downloadedFilePath.FileExists) return null;

            var settings = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>();
            var folder = settings.Folder.ToPath(ServiceProvider.GetRequiredService<IFileSystem>());
            var name = await GetMeaningfulFileNameAsync(downloadedFilePath, ct);
            return await DownloadsFolder.PlaceAsync(downloadedFilePath, folder, name, ct);
        }
        catch (Exception ex)
        {
            var logger = ServiceProvider.GetService<ILogger<AddDownloadJob>>();
            logger?.LogWarning(ex, "No se pudo preservar el archivo original descargado");
            return null;
        }
    }

    /// <summary>
    /// Tries to get a meaningful filename from NexusMods metadata, the download URI,
    /// or falls back to the temp file name. Detects file type via magic bytes if extension is missing.
    /// </summary>
    private async Task<string> GetMeaningfulFileNameAsync(AbsolutePath tempFilePath, CancellationToken ct)
    {
        var filename = string.Empty;

        // 1. Try to get filename from HTTP headers (Content-Disposition)
        // Check if the job is an HTTP job directly
        if (DownloadJob.JobDefinition is IHttpDownloadJob httpJob && httpJob.GetJobStateData() is IHttpDownloadState state && state.FileName.HasValue)
        {
            filename = state.FileName.Value.ToString();
        }
        // Check if the job is a Nexus job that contains an HTTP job
        else if (DownloadJob.JobDefinition is INexusModsDownloadJob nxmJob && nxmJob.HttpDownloadJob.JobDefinition.GetJobStateData() is IHttpDownloadState state2 && state2.FileName.HasValue)
        {
            filename = state2.FileName.Value.ToString();
        }

        // 2. Prioritize NexusMods file metadata name if we still don't have a filename
        if (string.IsNullOrEmpty(filename) && DownloadJob.JobDefinition is INexusModsDownloadJob nxmJob2)
        {
            try
            {
                var metadataName = nxmJob2.FileMetadata.Name;
                if (!string.IsNullOrEmpty(metadataName))
                    filename = metadataName;
            }
            catch
            {
                // Metadata might not be available
            }
        }

        // A collection package has no Content-Disposition or metadata name; a stable name lets PlaceAsync reuse
        // the file instead of keeping a "<guid>.tmp" copy per download
        if (string.IsNullOrEmpty(filename) && DownloadJob.JobDefinition is NexusModsCollectionDownloadJob collectionJob)
            filename = $"{collectionJob.Slug}-{collectionJob.Revision}";

        // 3. Fall back to extracting filename from HTTP URI
        if (string.IsNullOrEmpty(filename))
        {
            IHttpDownloadJob? httpJobFallback = DownloadJob.JobDefinition switch
            {
                IHttpDownloadJob h => h,
                INexusModsDownloadJob n => n.HttpDownloadJob.JobDefinition,
                _ => null
            };

            if (httpJobFallback != null)
            {
                try
                {
                    var uriPath = httpJobFallback.Uri.AbsolutePath;
                    var lastSegment = uriPath.Split('/').LastOrDefault(s => !string.IsNullOrEmpty(s));
                    if (!string.IsNullOrEmpty(lastSegment) && lastSegment.Contains('.'))
                    {
                        filename = Uri.UnescapeDataString(lastSegment);
                    }
                }
                catch
                {
                    // Ignore URI parsing errors
                }
            }
        }

        if (string.IsNullOrEmpty(filename))
            filename = tempFilePath.FileName;

        // The metadata name is a display name without extension; the real one is in the download URI
        var downloadUri = DownloadJob.JobDefinition switch
        {
            IHttpDownloadJob h => h.Uri,
            INexusModsDownloadJob n => n.HttpDownloadJob.JobDefinition.Uri,
            NexusModsCollectionDownloadJob c => c.DownloadJob?.JobDefinition.Uri,
            _ => null,
        };
        if (downloadUri is not null)
            filename = DownloadsFolder.WithExtensionFrom(filename, downloadUri);

        // Ensure we have an extension if the temp file has one, but AVOID .tmp
        var ext = tempFilePath.Extension;
        var extStr = ext.ToString();
        if (ext != Extension.None && !extStr.Equals(".tmp", StringComparison.OrdinalIgnoreCase) && !filename.EndsWith(extStr, StringComparison.OrdinalIgnoreCase))
        {
            if (!Path.HasExtension(filename))
                filename += extStr;
        }

        // If still no extension, detect via magic bytes
        if (!Path.HasExtension(filename) && tempFilePath.FileExists && tempFilePath.FileInfo.Size > Size.Zero)
        {
            try
            {
                var factory = ServiceProvider.GetService<ISignatureCheckerFactory>();
                if (factory != null)
                {
                    var checker = factory.Create(FileType._7Z, FileType.ZIP, FileType.RAR, FileType.RAR_NEW, FileType.RAR_OLD);
                    await using var stream = tempFilePath.Read();
                    var types = await checker.MatchesAsync(stream);
                    var detectedExt = types.Count > 0 ? types[0] switch
                    {
                        FileType._7Z => ".7z",
                        FileType.ZIP => ".zip",
                        FileType.RAR or FileType.RAR_NEW or FileType.RAR_OLD => ".rar",
                        _ => (string?)null
                    } : null;
                    if (detectedExt != null)
                        filename += detectedExt;
                }
            }
            catch
            {
                // Ignore detection errors - cosmetic only
            }
        }

        return filename;
    }
}
