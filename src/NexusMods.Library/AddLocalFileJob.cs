using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.Library.Jobs;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;

namespace NexusMods.Library;

internal class AddLocalFileJob : IJobDefinitionWithStart<AddLocalFileJob, LocalFile.ReadOnly>, IAddLocalFile
{
    public required AbsolutePath FilePath { get; init; }
    public required LocalFileMetadata Metadata { get; init; }
    internal required IConnection Connection { get; init; }
    internal required IServiceProvider ServiceProvider { get; init; }
    /// <summary>Root of the downloads folder. Not named DownloadsFolder: that is the static helper class.</summary>
    internal required AbsolutePath DownloadsRoot { get; init; }
    internal required ILogger<AddLocalFileJob> Logger { get; init; }

    public static IJobTask<AddLocalFileJob, LocalFile.ReadOnly> Create(IServiceProvider provider, AbsolutePath filePath, LocalFileMetadata metadata)
    {
        var monitor = provider.GetRequiredService<IJobMonitor>();
        var job = new AddLocalFileJob
        {
            FilePath = filePath,
            Metadata = metadata,
            Connection = provider.GetRequiredService<IConnection>(),
            ServiceProvider = provider,
            DownloadsRoot = provider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(provider.GetRequiredService<IFileSystem>()),
            Logger = provider.GetRequiredService<ILogger<AddLocalFileJob>>(),
        };
        return monitor.Begin<AddLocalFileJob, LocalFile.ReadOnly>(job);
    }

    public async ValueTask<LocalFile.ReadOnly> StartAsync(IJobContext<AddLocalFileJob> context)
    {
        var ct = context.CancellationToken;
        // File.Exists is false for directories and for symlinks to directories, so this also rejects a
        // link to a folder before anything is copied. A symlink to a file passes and is copied as a
        // regular file by PlaceAsync (it reads the content, it never links).
        if (!FilePath.FileExists)
            throw new InvalidOperationException($"No es un archivo o no existe: {FilePath}");

        // 1. Same content already in the library as a local file with a live download: reuse it
        //    before copying anything. Hashing the source first means this job never has to delete
        //    a copy afterwards, so it can never remove a file in Downloads that belongs to another
        //    item, nor the user's own original when Downloads is a symlink to the picked folder.
        var hash = await HashOf(FilePath, ct);
        var existing = LibraryFile.FindByHash(Connection.Db, hash)
            .Select(file => LocalFile.Load(Connection.Db, file.Id))
            .FirstOrDefault(local => local.IsValid());
        if (existing.IsValid() && HasLiveDownload(existing))
            return await ReuseExisting(existing, adopt: null);

        // 2. Copy into Downloads unless it already lives there. The user's file is never moved.
        var final = FilePath.InFolder(DownloadsRoot)
            ? FilePath
            : await DownloadsFolder.PlaceAsync(FilePath, DownloadsRoot, FilePath.FileName, ct);

        // 3. An old LocalFile with the same content but no usable download adopts this copy.
        if (existing.IsValid())
            return await ReuseExisting(existing, adopt: final);

        // 4. Register the copy. AddLibraryFileJob records DownloadPath because `final` is inside Downloads.
        using var tx = Connection.BeginTransaction();
        var libraryFile = await AddLibraryFileJob.Create(ServiceProvider, tx, final);
        var localFile = new LocalFile.New(tx, libraryFile.LibraryFileId)
        {
            LibraryFile = libraryFile,
            OriginalPath = FilePath.ToString(),
        };
        ApplyMetadata(tx, libraryFile.Id, Metadata);
        var result = await tx.Commit();
        return result.Remap(localFile);
    }

    private bool HasLiveDownload(LocalFile.ReadOnly local) =>
        LibraryFile.DownloadPath.TryGetValue(local.AsLibraryFile(), out var rel) && DownloadsRoot.Combine(rel).FileExists;

    /// <summary>
    /// Returns the existing item. With <paramref name="adopt"/> set, that copy becomes its download
    /// (an old LocalFile registered from outside Downloads, or one whose download is gone).
    /// </summary>
    private async ValueTask<LocalFile.ReadOnly> ReuseExisting(LocalFile.ReadOnly existing, AbsolutePath? adopt)
    {
        var name = existing.AsLibraryFile().AsLibraryItem().Name;
        using var tx = Connection.BeginTransaction();
        if (adopt is { } final)
        {
            tx.Add(existing.Id, LibraryFile.DownloadPath, final.RelativeTo(DownloadsRoot));
            Logger.LogInformation("'{File}' repara la descarga faltante de '{Existing}'", final, name);
        }
        else
        {
            Logger.LogInformation("'{File}' ya estaba en la biblioteca como '{Existing}'", FilePath, name);
        }
        ApplyMetadata(tx, existing.Id, Metadata);
        await tx.Commit();
        return LocalFile.Load(Connection.Db, existing.Id);
    }

    /// <summary>
    /// Writes the non-blank fields of <paramref name="metadata"/>. Blank values are skipped, never stored.
    /// </summary>
    internal static void ApplyMetadata(ITransaction tx, EntityId id, LocalFileMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.Name)) tx.Add(id, LibraryItem.Name, metadata.Name.Trim());
        if (!string.IsNullOrWhiteSpace(metadata.Version)) tx.Add(id, LocalFile.Version, metadata.Version.Trim());
        if (!string.IsNullOrWhiteSpace(metadata.Source)) tx.Add(id, LocalFile.Source, metadata.Source.Trim());
        if (metadata.PageUri is not null) tx.Add(id, LocalFile.PageUri, metadata.PageUri);
    }

    private static async Task<Hash> HashOf(AbsolutePath path, CancellationToken ct)
    {
        await using var stream = path.Read();
        return await stream.xxHash3Async(token: ct);
    }
}
