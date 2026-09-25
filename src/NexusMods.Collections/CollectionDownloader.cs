using System.Diagnostics;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Collections;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Sdk.Settings;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.DatomIterators;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.MnemonicDB.Abstractions.IndexSegments;
using NexusMods.MnemonicDB.Abstractions.Query;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using OneOf;
using Reloaded.Memory.Extensions;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Hashes;

namespace NexusMods.Collections;

/// <summary>
/// Methods for collection downloads.
/// </summary>
[PublicAPI]
public class CollectionDownloader
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;
    private readonly IConnection _connection;
    private readonly ILoginManager _loginManager;
    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly NexusModsLibrary _nexusModsLibrary;
    private readonly ILibraryService _libraryService;
    private readonly IOSInterop _osInterop;
    private readonly HttpClient _httpClient;
    private readonly IJobMonitor _jobMonitor;
    private readonly IGameDomainToGameIdMappingCache _mappingCache;
    private readonly IFileStore _fileStore;

    /// <summary>
    /// Constructor.
    /// </summary>
    public CollectionDownloader(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = serviceProvider.GetRequiredService<ILogger<CollectionDownloader>>();
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _loginManager = serviceProvider.GetRequiredService<ILoginManager>();
        _temporaryFileManager = serviceProvider.GetRequiredService<TemporaryFileManager>();
        _nexusModsLibrary = serviceProvider.GetRequiredService<NexusModsLibrary>();
        _libraryService = serviceProvider.GetRequiredService<ILibraryService>();
        _osInterop = serviceProvider.GetRequiredService<IOSInterop>();
        _httpClient = serviceProvider.GetRequiredService<HttpClient>();
        _jobMonitor = serviceProvider.GetRequiredService<IJobMonitor>();
        _mappingCache = serviceProvider.GetRequiredService<IGameDomainToGameIdMappingCache>();
        _fileStore = serviceProvider.GetRequiredService<IFileStore>();
    }

    /// <summary>
    /// Gets or adds a revision.
    /// </summary>
    public async ValueTask<CollectionRevisionMetadata.ReadOnly> GetOrAddRevision(CollectionSlug slug, RevisionNumber revisionNumber, CancellationToken cancellationToken)
    {
        var revisions = CollectionRevisionMetadata
            .FindByRevisionNumber(_connection.Db, revisionNumber)
            .Where(r => r.Collection.Slug == slug);

        if (revisions.TryGetFirst(out var revision)) return revision;

        await using var destination = _temporaryFileManager.CreateFile();
        var downloadJob = _nexusModsLibrary.CreateCollectionDownloadJob(destination, slug, revisionNumber, CancellationToken.None);

        var libraryFile = await _libraryService.AddDownload(downloadJob);

        if (!libraryFile.TryGetAsNexusModsCollectionLibraryFile(out var collectionFile))
            throw new InvalidOperationException("The library file is not a NexusModsCollectionLibraryFile");

        revision = await _nexusModsLibrary.GetOrAddCollectionRevision(collectionFile, slug, revisionNumber, cancellationToken);
        return revision;
    }

    record DirectDownloadResult(bool CanDownload, Optional<RelativePath> FileName = default)
    {
        public static readonly DirectDownloadResult Unable = new(CanDownload: false);
    };

    private async ValueTask<DirectDownloadResult> CanDirectDownload(
        CollectionDownloadExternal.ReadOnly download,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Testing if `{Uri}` can be downloaded directly", download.Uri);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, download.Uri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode) return DirectDownloadResult.Unable;

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is null || !contentType.StartsWith("application/"))
            {
                _logger.LogInformation("Download at `{Uri}` can't be downloaded automatically because Content-Type `{ContentType}` doesn't indicate a binary download", download.Uri, contentType);
                return DirectDownloadResult.Unable;
            }

            if (!response.Content.Headers.ContentLength.HasValue)
            {
                _logger.LogInformation("Download at `{Uri}` can't be downloaded automatically because the response doesn't have a Content-Length", download.Uri);
                return DirectDownloadResult.Unable;
            }

            var size = Size.FromLong(response.Content.Headers.ContentLength.Value);
            if (size != download.Size)
            {
                _logger.LogWarning("Download at `{Uri}` can't be downloaded automatically because the Content-Length `{ContentLength}` doesn't match the expected size `{ExpectedSize}`", download.Uri, size, download.Size);
                return DirectDownloadResult.Unable;
            }

            var contentDispositionFileName = response.Content.Headers.ContentDisposition?.FileName;
            var fileName = contentDispositionFileName is null ? Optional<RelativePath>.None : RelativePath.FromUnsanitizedInput(contentDispositionFileName);

            return new DirectDownloadResult(CanDownload: true, FileName: fileName);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception while checking if `{Uri}` can be downloaded directly", download.Uri);
            return DirectDownloadResult.Unable;
        }
    }

    /// <summary>
    /// Downloads an external file.
    /// </summary>
    public async ValueTask Download(CollectionDownloadExternal.ReadOnly download, CancellationToken cancellationToken)
    {
        var result = await CanDirectDownload(download, cancellationToken);
        if (result.CanDownload)
        {
            _logger.LogInformation("Downloading external file directly at `{Uri}` (`{Hash}`)", download.Uri, download.Md5);

            if (download.Rebase(_connection.Db).IsManualOnly)
            {
                using var tx = _connection.BeginTransaction();
                tx.Retract(download.Id, CollectionDownloadExternal.ManualOnly, Null.Instance);
                await tx.Commit();
            }

            var job = ExternalDownloadJob.Create(
                _serviceProvider,
                download.Uri,
                download.Md5,
                logicalFileName: download.AsCollectionDownload().Name,
                fileName: result.FileName
            );

            await _libraryService.AddDownload(job);
        }
        else
        {
            _logger.LogInformation("Unable to direct download `{Uri}` (`{Hash}`)", download.Uri, download.Md5);
            if (download.Rebase(_connection.Db).IsManualOnly) return;

            using var tx = _connection.BeginTransaction();
            tx.Add(download.Id, CollectionDownloadExternal.ManualOnly, Null.Instance);
            await tx.Commit();
        }
    }

    /// <summary>
    /// Downloads a file from nexus mods. For premium users uses the API directly; for others
    /// attempts a direct download via the website endpoint and falls back to the browser.
    /// </summary>
    public async ValueTask Download(CollectionDownloadNexusMods.ReadOnly download, CancellationToken cancellationToken, bool openBrowserOnFailure = true)
    {
        var userInfo = await _loginManager.GetUserInfoAsync(cancellationToken);
        if (userInfo is null) return;

        if (userInfo.UserRole is UserRole.Premium)
        {
            await using var tempPath = _temporaryFileManager.CreateFile();
            var job = await _nexusModsLibrary.CreateDownloadJob(tempPath, download.FileMetadata, parentRevision: download.AsCollectionDownload().CollectionRevision, cancellationToken: cancellationToken);
            var jobTask = _libraryService.AddDownload(job);
            using (cancellationToken.Register(() => _jobMonitor.Cancel(jobTask)))
                await jobTask;
        }
        else
        {
            // Try direct download via the website endpoint (works for logged-in free/supporter users)
            try
            {
                await using var tempPath = _temporaryFileManager.CreateFile();
                var job = await _nexusModsLibrary.CreateDownloadJob(tempPath, download.FileMetadata, parentRevision: download.AsCollectionDownload().CollectionRevision, cancellationToken: cancellationToken);
                // Register cancellation to cancel the child job properly instead of using WaitAsync(token).
                // WaitAsync(token) would unblock early and dispose the temp file while AddLibraryFileJob is still running.
                var jobTask = _libraryService.AddDownload(job);
                using (cancellationToken.Register(() => _jobMonitor.Cancel(jobTask)))
                    await jobTask; // Wait for full completion (job will cancel itself quickly if token fires)
                return;
            }
            catch (OperationCanceledException)
            {
                throw; // propagate cancellation — don't open browser, don't swallow
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Direct download failed for file {FileId}", download.FileUid.FileId);
            }

            if (openBrowserOnFailure)
            {
                var domain = _mappingCache[download.FileUid.GameId];
                _osInterop.OpenUri(NexusModsUrlBuilder.GetFileDownloadUri(domain, download.ModUid.ModId, download.FileUid.FileId, useNxmLink: true, campaign: NexusModsUrlBuilder.CampaignCollections));
            }
            else
            {
                _logger.LogWarning("Direct download failed for file {FileId} in batch mode — skipping (retry by clicking Download All again)", download.FileUid.FileId);
            }
        }
    }

    /// <summary>
    /// Returns an observable with the number of downloaded items.
    /// </summary>
    public IObservable<int> DownloadedItemCountObservable(CollectionRevisionMetadata.ReadOnly revisionMetadata, ItemType itemType)
    {
        return _connection
            .ObserveDatoms(CollectionDownload.CollectionRevision, revisionMetadata)
            .AsEntityIds()
            .Transform(datom => CollectionDownload.Load(_connection.Db, datom.E))
            .FilterImmutable(download => DownloadMatchesItemType(download, itemType))
            .TransformOnObservable(download => GetStatusObservable(download, Observable.Return(Optional<CollectionGroup.ReadOnly>.None)))
            .FilterImmutable(static status => status.IsDownloaded() && !status.IsBundled())
            .QueryWhenChanged(query => query.Count)
            .Prepend(0);
    }

    /// <summary>
    /// Counts the items.
    /// </summary>
    public static int CountItems(CollectionRevisionMetadata.ReadOnly revisionMetadata, ItemType itemType)
    {
        return revisionMetadata.Downloads
            .Where(download => DownloadMatchesItemType(download, itemType))
            .Count(download => download.IsCollectionDownloadNexusMods() || download.IsCollectionDownloadExternal());
    }

    /// <summary>
    /// Returns whether the item matches the given item type.
    /// </summary>
    internal static bool DownloadMatchesItemType(CollectionDownload.ReadOnly download, ItemType itemType)
    {
        if (download.IsOptional && itemType.HasFlagFast(ItemType.Optional)) return true;
        if (download.IsRequired && itemType.HasFlagFast(ItemType.Required)) return true;
        return false;
    }

    /// <summary>
    /// Checks whether the items in the collection were downloaded.
    /// </summary>
    public static bool IsFullyDownloaded(CollectionDownload.ReadOnly[] items, IDb db)
    {
        return items.All(download => GetStatus(download, db).IsDownloaded());
    }
    
    public static bool IsFullyInstalled(CollectionDownload.ReadOnly[] items, Optional<CollectionGroup.ReadOnly> collectionGroup, IDb db)
    {
        return items.All(download => GetStatus(download, collectionGroup, db).IsInstalled(out _));
    }

    [Flags, PublicAPI]
    public enum ItemType
    {
        Required = 1,
        Optional = 2,
    };

    /// <summary>
    /// Downloads everything in the revision.
    /// </summary>
    public async ValueTask DownloadItems(
        CollectionRevisionMetadata.ReadOnly revisionMetadata,
        ItemType itemType,
        IDb db,
        CancellationToken cancellationToken = default)
    {
        var job = new DownloadCollectionJob
        {
            Downloader = this,
            Logger = _serviceProvider.GetRequiredService<ILogger<DownloadCollectionJob>>(),
            RevisionMetadata = revisionMetadata,
            Db = db,
            ItemType = itemType,
            MaxDegreeOfParallelism = _serviceProvider.GetRequiredService<ISettingsManager>().Get<DownloadSettings>().MaxParallelDownloads,
        };

        await _jobMonitor.Begin<DownloadCollectionJob, R3.Unit>(job);
    }

    /// <summary>
    /// Checks whether the collection is installed.
    /// </summary>
    public IObservable<bool> IsCollectionInstalledObservable(
        CollectionRevisionMetadata.ReadOnly revision, 
        IObservable<Optional<CollectionGroup.ReadOnly>> groupObservable, 
        ItemType itemType = ItemType.Required)
    {
        var observables = revision.Downloads
            .Where(download => DownloadMatchesItemType(download, itemType))
            .Select(download => GetStatusObservable(download, groupObservable).Select(static status => status.IsInstalled(out _)))
            .ToArray();

        if (observables.Length == 0) return groupObservable.Select(static optional => optional.HasValue);
        return observables.CombineLatest(static list => list.All(static installed => installed));
    }

    /// <summary>
    /// Returns all missing downloads and Uris.
    /// </summary>
    public IReadOnlyList<(CollectionDownload.ReadOnly Download, Uri Uri)> GetMissingDownloadLinks(CollectionRevisionMetadata.ReadOnly revision, IDb db, ItemType itemType = ItemType.Required)
    {
        var results = new List<(CollectionDownload.ReadOnly Download, Uri Uri)>();
        var downloads = GetItems(revision, itemType).Where(download => GetStatus(download, db).IsNotDownloaded());

        foreach (var download in downloads)
        {
            if (download.TryGetAsCollectionDownloadNexusMods(out var nexusModsDownload))
            {
                var domain = _mappingCache[nexusModsDownload.FileUid.GameId];
                var uri = NexusModsUrlBuilder.GetFileDownloadUri(domain, nexusModsDownload.ModUid.ModId, nexusModsDownload.FileUid.FileId, useNxmLink: false, source: null);
                results.Add((download, uri));
            } else if (download.TryGetAsCollectionDownloadExternal(out var externalDownload))
            {
                results.Add((download, externalDownload.Uri));
            }
        }

        return results;
    }

    private static CollectionDownloadStatus GetStatus(CollectionDownloadBundled.ReadOnly download, Optional<CollectionGroup.ReadOnly> collectionGroup, IDb db)
    {
        if (!collectionGroup.HasValue) return new CollectionDownloadStatus.Bundled();

        var entityIds = db.Datoms(
            (NexusCollectionBundledLoadoutGroup.BundleDownload, download),
            (LoadoutItem.ParentId, collectionGroup.Value)
        );

        foreach (var entityId in entityIds)
        {
            var loadoutItem = LoadoutItem.Load(db, entityId);
            if (loadoutItem.IsValid()) return new CollectionDownloadStatus.Installed(loadoutItem);
        }

        return new CollectionDownloadStatus.Bundled();
    }

    private IObservable<CollectionDownloadStatus> GetStatusObservable(
        CollectionDownloadBundled.ReadOnly download,
        IObservable<Optional<CollectionGroup.ReadOnly>> groupObservable)
    {
        return _connection
            .ObserveDatoms(NexusCollectionBundledLoadoutGroup.BundleDownload, download)
            .TransformImmutable(datom => LoadoutItem.Load(_connection.Db, datom.E))
            .FilterOnObservable(item =>
            {
                return groupObservable
                    .Select(optional => optional.Convert(static group => group.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId))
                    .Select(loadoutId => loadoutId.HasValue && item.LoadoutId == loadoutId.Value);
            })
            .QueryWhenChanged(query => query.Items.FirstOrOptional(static _ => true))
            .Select(optional =>
            {
                if (!optional.HasValue) return (CollectionDownloadStatus) new CollectionDownloadStatus.Bundled();
                return new CollectionDownloadStatus.Installed(optional.Value);
            })
            .Prepend(new CollectionDownloadStatus.Bundled());
    }

    private static CollectionDownloadStatus GetStatus(CollectionDownloadNexusMods.ReadOnly download, Optional<CollectionGroup.ReadOnly> collectionGroup, IDb db)
    {
        var datoms = db.Datoms(NexusModsLibraryItem.FileMetadata, download.FileMetadata);
        if (datoms.Count == 0) return new CollectionDownloadStatus.NotDownloaded();

        var libraryItem = default(NexusModsLibraryItem.ReadOnly);
        foreach (var datom in datoms)
        {
            libraryItem = NexusModsLibraryItem.Load(db, datom.E);
            if (libraryItem.IsValid()) break;
        }

        if (!libraryItem.IsValid()) return new CollectionDownloadStatus.NotDownloaded();
        return GetStatus(libraryItem.AsLibraryItem(), collectionGroup, db);
    }

    private IObservable<CollectionDownloadStatus> GetStatusObservable(
        CollectionDownloadNexusMods.ReadOnly download,
        IObservable<Optional<CollectionGroup.ReadOnly>> groupObservable)
    {
        return _connection
            .ObserveDatoms(NexusModsLibraryItem.FileMetadata, download.FileMetadata)
            .QueryWhenChanged(query => query.Items.FirstOrOptional(static _ => true))
            .DistinctUntilChanged(OptionalDatomComparer.Instance)
            .SelectMany(optional =>
            {
                if (!optional.HasValue) return Observable.Return<CollectionDownloadStatus>(new CollectionDownloadStatus.NotDownloaded());

                var libraryItem = LibraryItem.Load(_connection.Db, optional.Value.E);
                Debug.Assert(libraryItem.IsValid());

                return GetStatusObservable(libraryItem, groupObservable);
            });
    }

    private static CollectionDownloadStatus GetStatus(CollectionDownloadExternal.ReadOnly download, Optional<CollectionGroup.ReadOnly> collectionGroup, IDb db)
    {
        var datoms = db.Datoms(LibraryFile.Md5, download.Md5);
        if (datoms.Count == 0) return new CollectionDownloadStatus.NotDownloaded();

        foreach (var datom in datoms)
        {
            var libraryFile = DirectDownloadLibraryFile.Load(db, datom.E).AsLocalFile().AsLibraryFile();
            if (libraryFile.IsValid()) return GetStatus(libraryFile.AsLibraryItem(), collectionGroup, db);
        }

        return new CollectionDownloadStatus.NotDownloaded();
    }

    private IObservable<CollectionDownloadStatus> GetStatusObservable(
        CollectionDownloadExternal.ReadOnly download,
        IObservable<Optional<CollectionGroup.ReadOnly>> groupObservable)
    {
        var observable = _connection.ObserveDatoms(SliceDescriptor.Create(LibraryFile.Md5, download.Md5, _connection.AttributeCache));

        return observable
            .QueryWhenChanged(query => query.Items.FirstOrOptional(static _ => true))
            .Prepend(Optional<Datom>.None)
            .DistinctUntilChanged(OptionalDatomComparer.Instance)
            .SelectMany(optional =>
            {
                if (!optional.HasValue) return Observable.Return<CollectionDownloadStatus>(new CollectionDownloadStatus.NotDownloaded());

                var libraryItem = LibraryItem.Load(_connection.Db, optional.Value.E);
                Debug.Assert(libraryItem.IsValid());

                return GetStatusObservable(libraryItem, groupObservable);
            });
    }

    private static CollectionDownloadStatus GetStatus(
        LibraryItem.ReadOnly libraryItem,
        Optional<CollectionGroup.ReadOnly> collectionGroup,
        IDb db)
    {
        if (!collectionGroup.HasValue) return new CollectionDownloadStatus.InLibrary(libraryItem);

        var entityIds = db.Datoms(
            (LibraryLinkedLoadoutItem.LibraryItem, libraryItem),
            (LoadoutItem.ParentId, collectionGroup.Value)
        );

        if (entityIds.Count == 0) return new CollectionDownloadStatus.InLibrary(libraryItem);

        foreach (var entityId in entityIds)
        {
            var loadoutItem = LoadoutItem.Load(db, entityId);
            if (!loadoutItem.IsValid()) continue;
            return new CollectionDownloadStatus.Installed(loadoutItem);
        }

        return new CollectionDownloadStatus.InLibrary(libraryItem);
    }

    private IObservable<CollectionDownloadStatus> GetStatusObservable(
        LibraryItem.ReadOnly libraryItem,
        IObservable<Optional<CollectionGroup.ReadOnly>> groupObservable)
    {
        return _connection
            .ObserveDatoms(LibraryLinkedLoadoutItem.LibraryItemId, libraryItem.LibraryItemId)
            .TransformImmutable(datom => LibraryLinkedLoadoutItem.Load(_connection.Db, datom.E))
            .FilterOnObservable(item =>
            {
                return groupObservable
                    .Select(group =>
                    {
                        if (!group.HasValue) return false;
                        var itemLoadoutId = LoadoutItem.LoadoutId.Get(item);
                        var groupLoadoutId = LoadoutItem.LoadoutId.Get(group.Value);
                        var parentId = LoadoutItem.ParentId.Get(item);
                        var id = group.Value.Id;

                        return itemLoadoutId == groupLoadoutId && parentId == id;
                    });
            })
            .QueryWhenChanged(query =>
            {
                var optional = query.Items.FirstOrOptional(static x => true);

                CollectionDownloadStatus status = optional.HasValue
                    ? new CollectionDownloadStatus.Installed(optional.Value.AsLoadoutItemGroup().AsLoadoutItem())
                    : new CollectionDownloadStatus.InLibrary(libraryItem);

                return status;
            })
            .Prepend(new CollectionDownloadStatus.InLibrary(libraryItem));
    }

    /// <summary>
    /// Gets the status of a download as an observable.
    /// </summary>
    public IObservable<CollectionDownloadStatus> GetStatusObservable(
        CollectionDownload.ReadOnly download,
        IObservable<Optional<CollectionGroup.ReadOnly>> groupObservable)
    {
        IObservable<CollectionDownloadStatus> statusObservable;

        if (download.TryGetAsCollectionDownloadBundled(out var bundled))
        {
            statusObservable = GetStatusObservable(bundled, groupObservable);
        }
        else if (download.TryGetAsCollectionDownloadNexusMods(out var nexusModsDownload))
        {
            statusObservable = GetStatusObservable(nexusModsDownload, groupObservable);
        }
        else if (download.TryGetAsCollectionDownloadExternal(out var externalDownload))
        {
            statusObservable = GetStatusObservable(externalDownload, groupObservable);
        }
        else
        {
            throw new NotSupportedException();
        }

        // Validate that "InLibrary" items actually have their archive on disk.
        // If not, downgrade to "NotDownloaded" so the UI shows the download button.
        return statusObservable
            .SelectMany(status => Observable.FromAsync(() => ValidateStatusAsync(status).AsTask()))
            .DistinctUntilChanged();
    }

    /// <summary>
    /// Gets the status of a download.
    /// </summary>
    public static CollectionDownloadStatus GetStatus(CollectionDownload.ReadOnly download, IDb db)
    {
        return GetStatus(download, new Optional<CollectionGroup.ReadOnly>(), db);
    }

    /// <summary>
    /// Gets the status of a download.
    /// </summary>
    public static CollectionDownloadStatus GetStatus(
        CollectionDownload.ReadOnly download,
        Optional<CollectionGroup.ReadOnly> collectionGroup,
        IDb db)
    {
        if (download.TryGetAsCollectionDownloadBundled(out var bundled))
        {
            return GetStatus(bundled, collectionGroup, db);
        }

        if (download.TryGetAsCollectionDownloadNexusMods(out var nexusModsDownload))
        {
            return GetStatus(nexusModsDownload, collectionGroup, db);
        }

        if (download.TryGetAsCollectionDownloadExternal(out var externalDownload))
        {
            return GetStatus(externalDownload, collectionGroup, db);
        }

        throw new NotSupportedException();
    }

    /// <summary>
    /// Validates that a status reporting "InLibrary" or "Installed" actually has its files on disk: either
    /// the original download (in <see cref="DownloadsSettings.Folder"/>) or every entry in the file store.
    /// If both are missing, downgrades the status to "NotDownloaded" so the UI
    /// shows the download button and the user can re-download.
    /// </summary>
    public async ValueTask<CollectionDownloadStatus> ValidateStatusAsync(CollectionDownloadStatus status)
    {
        // Extract the LibraryItem from either InLibrary or Installed status
        LibraryItem.ReadOnly libraryItem;
        if (status.IsInLibrary(out libraryItem))
        {
            // Already have the library item
        }
        else if (status.IsInstalled(out var loadoutItem))
        {
            // Trace back: LoadoutItem → LibraryLinkedLoadoutItem → LibraryItem
            var linked = LibraryLinkedLoadoutItem.Load(_connection.Db, loadoutItem.Id);
            if (!linked.IsValid())
                return status; // Not library-linked, can't validate
            libraryItem = linked.LibraryItem;
        }
        else
        {
            return status;
        }

        if (!libraryItem.TryGetAsLibraryFile(out var libraryFile)) return status;

        // The original download is still on disk: whatever is missing from the store is re-extracted
        // from it at install time, so the item is downloaded.
        if (IsDownloadOnDisk(libraryFile)) return status;

        try
        {
            // For archives (zip/7z/rar), the file store indexes the CONTENTS (children),
            // not the archive file itself. Check ALL children's hashes — HaveFile is just
            // a dictionary lookup so this is fast even for large archives.
            if (libraryFile.TryGetAsLibraryArchive(out var archive))
            {
                var children = archive.Children;
                foreach (var child in children)
                {
                    if (!await _fileStore.HaveFile(child.AsLibraryFile().Hash))
                    {
                        _logger.LogWarning("[VALIDATE] Archive '{Name}' has missing content (file '{Path}', hash={Hash}) — marking as NotDownloaded",
                            libraryItem.Name, child.Path, child.AsLibraryFile().Hash);
                        return new CollectionDownloadStatus.NotDownloaded();
                    }
                }
                return status;
            }

            // For plain files (non-archives), check the file hash directly
            if (!await _fileStore.HaveFile(libraryFile.Hash))
            {
                _logger.LogWarning("[VALIDATE] Library file '{Name}' (hash={Hash}) missing from file store — marking as NotDownloaded",
                    libraryItem.Name, libraryFile.Hash);
                return new CollectionDownloadStatus.NotDownloaded();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[VALIDATE] Error checking archive existence for '{Name}', assuming available", libraryItem.Name);
        }

        return status;
    }

    private bool IsDownloadOnDisk(LibraryFile.ReadOnly libraryFile)
    {
        if (!LibraryFile.DownloadPath.TryGetValue(libraryFile, out var relativePath)) return false;
        var downloads = _serviceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder
            .ToPath(_serviceProvider.GetRequiredService<IFileSystem>());
        return downloads.Combine(relativePath).FileExists;
    }

    /// <summary>
    /// Deletes all associated collection loadout groups.
    /// </summary>
    public async ValueTask DeleteCollectionLoadoutGroup(CollectionRevisionMetadata.ReadOnly revision, CancellationToken cancellationToken)
    {
        var db = _connection.Db;
        using var tx = _connection.BeginTransaction();

        var groupDatoms = db.Datoms(NexusCollectionLoadoutGroup.Revision, revision);
        foreach (var datom in groupDatoms)
        {
            tx.Delete(datom.E, recursive: true);
        }

        await tx.Commit();
    }

    /// <summary>
    /// Returns all items of the desired type (required/optional).
    /// </summary>
    public static CollectionDownload.ReadOnly[] GetItems(CollectionRevisionMetadata.ReadOnly revision, ItemType itemType)
    {
        var res = new CollectionDownload.ReadOnly[revision.Downloads.Count];

        var i = 0;
        foreach (var download in revision.Downloads)
        {
            if (!DownloadMatchesItemType(download, itemType)) continue;
            res[i++] = download;
        }

        Array.Resize(ref res, newSize: i);
        return res;
    }

    /// <summary>
    /// Gets the library file for the collection.
    /// </summary>
    public NexusModsCollectionLibraryFile.ReadOnly GetLibraryFile(CollectionRevisionMetadata.ReadOnly revisionMetadata)
    {
        var datoms = _connection.Db.Datoms(
            (NexusModsCollectionLibraryFile.CollectionSlug, revisionMetadata.Collection.Slug),
            (NexusModsCollectionLibraryFile.CollectionRevisionNumber, revisionMetadata.RevisionNumber)
        );

        if (datoms.Count == 0) throw new Exception($"Unable to find collection file for revision `{revisionMetadata.Collection.Slug}` (`{revisionMetadata.RevisionNumber}`)");
        var source = NexusModsCollectionLibraryFile.Load(_connection.Db, datoms[0]);
        return source;
    }

    /// <summary>
    /// Returns the collection group associated with the revision or none.
    /// </summary>
    public static Optional<NexusCollectionLoadoutGroup.ReadOnly> GetCollectionGroup(
        CollectionRevisionMetadata.ReadOnly revisionMetadata,
        Optional<LoadoutId> loadoutId,
        IDb db)
    {
        if (!loadoutId.HasValue) return Optional.None<NexusCollectionLoadoutGroup.ReadOnly>();

        var entityIds = db.Datoms(
            (NexusCollectionLoadoutGroup.Revision, revisionMetadata),
            (LoadoutItem.Loadout, loadoutId.Value)
        );

        if (entityIds.Count == 0) return Optional.None<NexusCollectionLoadoutGroup.ReadOnly>();
        foreach (var entityId in entityIds)
        {
            var group = NexusCollectionLoadoutGroup.Load(db, entityId);
            if (group.IsValid()) return group;
        }

        return new Optional<NexusCollectionLoadoutGroup.ReadOnly>();
    }

    /// <summary>
    /// Gets an observable stream containing the collection group associated with the revision.
    /// </summary>
    public IObservable<Optional<CollectionGroup.ReadOnly>> GetCollectionGroupObservable(CollectionRevisionMetadata.ReadOnly revision, Optional<LoadoutId> targetLoadout)
    {
        if (!targetLoadout.HasValue) return Observable.Return(Optional<CollectionGroup.ReadOnly>.None);

        return _connection
            .ObserveDatoms(NexusCollectionLoadoutGroup.Revision, revision)
            .QueryWhenChanged(query =>
            {
                // Multiple groups with the same revision can exist if a job was created from a stale DB
                // snapshot and created a duplicate before finding the existing one. Prefer the group that
                // has installed children (i.e., the one from the required install), falling back to any valid one.
                CollectionGroup.ReadOnly? fallback = null;
                foreach (var datom in query.Items)
                {
                    var group = CollectionGroup.Load(_connection.Db, datom.E);
                    if (!group.IsValid()) continue;
                    if (group.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId != targetLoadout.Value) continue;

                    var hasChildren = _connection.Db.Datoms(LoadoutItem.Parent, group.Id).Any();
                    if (hasChildren) return Optional<CollectionGroup.ReadOnly>.Create(group);

                    fallback ??= group;
                }

                return fallback.HasValue
                    ? Optional<CollectionGroup.ReadOnly>.Create(fallback.Value)
                    : Optional<CollectionGroup.ReadOnly>.None;
            })
            .Prepend(GetCollectionGroup(revision, targetLoadout, _connection.Db).Convert(static x => x.AsCollectionGroup()));
    }

    public async ValueTask RescanDownloads(CollectionRevisionMetadata.ReadOnly revision, CancellationToken ct)
    {
        _logger.LogInformation("Starting rescan of Downloads folder for collection `{CollectionName}`", revision.Collection.Name);
        
        var fs = _serviceProvider.GetRequiredService<IFileSystem>();
        var downloadsFolder = _serviceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(fs);
        if (!downloadsFolder.DirectoryExists())
        {
            _logger.LogWarning("Downloads folder does not exist: `{Path}`", downloadsFolder);
            return;
        }

        var db = _connection.Db;
        var files = downloadsFolder.EnumerateFiles().ToArray();
        _logger.LogInformation("Found {Count} files in Downloads folder", files.Length);
        
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            try 
            {
                // Skip files that are too small or likely not mods
                if (file.FileInfo.Size < Size.From(1024)) continue;

                // Hash the file
                Md5Value md5;
                await using (var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    md5 = await Md5Hasher.HashAsync(stream, cancellationToken: ct);
                }

                // Check if already in library
                var existingDatoms = db.Datoms(LibraryFile.Md5, md5);
                
                bool matched = false;
                foreach (var download in revision.Downloads)
                {
                    // 1. Match External downloads by MD5
                    if (download.TryGetAsCollectionDownloadExternal(out var external) && external.Md5 == md5)
                    {
                        _logger.LogInformation("Match found (External) for `{DownloadName}`: `{FilePath}`", download.Name, file);
                        if (existingDatoms.Count == 0) await _libraryService.AddLocalFile(file);
                        matched = true;
                        break;
                    }

                    // 2. Match Nexus downloads by filename (Exact match or match without extension)
                    if (download.TryGetAsCollectionDownloadNexusMods(out var nexus))
                    {
                        var metadata = nexus.FileMetadata;
                        var metadataName = metadata.Name;
                        var currentFileName = file.FileName.ToString();
                        var currentFileNameWithoutExt = Path.GetFileNameWithoutExtension(currentFileName);
                        
                        if (currentFileName.Equals(metadataName, StringComparison.OrdinalIgnoreCase) || 
                            currentFileNameWithoutExt.Equals(metadataName, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("Match found (Nexus) for `{DownloadName}`: `{FilePath}`", download.Name, file);
                            
                            var currentFile = file;

                            // TRY TO FIX MISSING EXTENSION
                            var ext = file.Extension;
                            if (ext == Extension.None || ext.ToString().Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                            {
                                var detectedExt = DetectExtension(file);
                                if (!string.IsNullOrEmpty(detectedExt))
                                {
                                    var newName = currentFileNameWithoutExt + detectedExt;
                                    var newPath = file.Parent.Combine(newName);
                                    if (!newPath.FileExists)
                                    {
                                        _logger.LogInformation("Renaming `{OldPath}` to `{NewPath}` (Detected extension: {Ext})", file, newPath, detectedExt);
                                        file.FileSystem.MoveFile(file, newPath, true);
                                        currentFile = newPath;
                                    }
                                }
                            }

                            // Get or add the library file
                            EntityId libraryFileId;
                            LibraryFile.ReadOnly libraryFile;
                            if (existingDatoms.Count == 0)
                            {
                                var result = await _libraryService.AddLocalFile(currentFile);
                                libraryFileId = result.Id;
                                libraryFile = new LibraryFile.ReadOnly(db, result.Id);
                            }
                            else
                            {
                                libraryFileId = existingDatoms[0].E;
                                libraryFile = new LibraryFile.ReadOnly(db, libraryFileId);
                                
                                // Update DB if filename changed
                                if (libraryFile.FileName != currentFile.FileName)
                                {
                                    using var txName = _connection.BeginTransaction();
                                    txName.Add(libraryFileId, LibraryFile.FileName, currentFile.FileName);
                                    await txName.Commit();
                                }
                            }

                            // FORCED LINKING: Ensure this library file is linked to the metadata
                            _logger.LogInformation("Ensuring library file `{LibraryFileId}` is linked to Nexus Metadata `{MetadataId}`", libraryFileId, metadata.Id);
                            using var tx = _connection.BeginTransaction();
                            
                            _ = new NexusModsLibraryItem.New(tx, libraryFileId)
                            {
                                LibraryItem = new LibraryItem.New(tx, libraryFileId) { Name = libraryFile.FileName.ToString() },
                                FileMetadataId = metadata.Id,
                                ModPageMetadataId = metadata.ModPageId,
                            };
                            await tx.Commit();

                            matched = true;
                            break;
                        }
                    }
                }

                if (!matched)
                {
                    _logger.LogDebug("No match found in collection for `{FileName}` (MD5: {Hash})", file.FileName, md5);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to hash or process file during rescan: `{FilePath}`", file);
            }
        }
        
        _logger.LogInformation("Rescan complete.");
    }

    private static string DetectExtension(AbsolutePath file)
    {
        try
        {
            using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] header = new byte[8];
            int read = stream.Read(header, 0, 8);
            if (read < 4) return string.Empty;

            // 7-Zip: 37 7A BC AF 27 1C
            if (header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC && header[3] == 0xAF) return ".7z";
            // ZIP: 50 4B 03 04
            if (header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04) return ".zip";
            // RAR: 52 61 72 21 1A 07
            if (header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21) return ".rar";

            return string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Deletes a revision and all downloaded entities.
    /// </summary>
    public async ValueTask DeleteRevision(CollectionRevisionMetadataId revisionId)
    {
        var db = _connection.Db;
        using var tx = _connection.BeginTransaction();

        var downloadIds = db.Datoms(CollectionDownload.CollectionRevision, revisionId);
        foreach (var downloadId in downloadIds)
        {
            tx.Delete(downloadId.E, recursive: false);
        }

        tx.Delete(revisionId, recursive: false);

        await tx.Commit();
    }

    /// <summary>
    /// Deletes a collection, all revisions, and all download entities of all revisions.
    /// </summary>
    public async ValueTask DeleteCollection(CollectionMetadataId collectionMetadataId)
    {
        var db = _connection.Db;
        using var tx = _connection.BeginTransaction();

        var revisionIds = db.Datoms(CollectionRevisionMetadata.CollectionId, collectionMetadataId);
        foreach (var revisionId in revisionIds)
        {
            var downloadIds = db.Datoms(CollectionDownload.CollectionRevision, revisionId.E);
            foreach (var downloadId in downloadIds)
            {
                tx.Delete(downloadId.E, recursive: false);
            }

            tx.Delete(revisionId.E, recursive: false);
        }

        tx.Delete(collectionMetadataId, recursive: false);

        await tx.Commit();
    }

    /// <summary>
    /// Returns all collections for the given game.
    /// </summary>
    public static CollectionMetadata.ReadOnly[] GetCollections(IDb db, NexusModsGameId nexusModsGameId)
    {
        return CollectionMetadata.FindByGameId(db, nexusModsGameId).ToArray();
    }
}

/// <summary>
/// Represents the current status of a download in a collection.
/// </summary>
[PublicAPI]
[DebuggerDisplay("{Value}")]
public readonly struct CollectionDownloadStatus : IEquatable<CollectionDownloadStatus>
{
    /// <summary>
    /// Value.
    /// </summary>
    public readonly OneOf<NotDownloaded, Bundled, InLibrary, Installed> Value;

    /// <summary>
    /// Constructor.
    /// </summary>
    public CollectionDownloadStatus(OneOf<NotDownloaded, Bundled, InLibrary, Installed> value)
    {
        Value = value;
    }

    /// <summary>
    /// Item hasn't been downloaded yet.
    /// </summary>
    public readonly struct NotDownloaded;

    /// <summary>
    /// For bundled downloads.
    /// </summary>
    public readonly struct Bundled;

    /// <summary>
    /// For items that have been downloaded and added to the library.
    /// </summary>
    public readonly struct InLibrary : IEquatable<InLibrary>
    {
        /// <summary>
        /// The library item.
        /// </summary>
        public readonly LibraryItem.ReadOnly LibraryItem;

        /// <summary>
        /// Constructor.
        /// </summary>
        public InLibrary(LibraryItem.ReadOnly libraryItem)
        {
            LibraryItem = libraryItem;
        }

        /// <inheritdoc/>
        public bool Equals(InLibrary other) => LibraryItem.LibraryItemId == other.LibraryItem.LibraryItemId;
        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is InLibrary other && Equals(other);
        /// <inheritdoc/>
        public override int GetHashCode() => LibraryItem.Id.GetHashCode();
    }

    /// <summary>
    /// For items that have been installed.
    /// </summary>
    public readonly struct Installed : IEquatable<Installed>
    {
        /// <summary>
        /// The loadout item.
        /// </summary>
        public readonly LoadoutItem.ReadOnly LoadoutItem;

        /// <summary>
        /// Constructor.
        /// </summary>
        public Installed(LoadoutItem.ReadOnly loadoutItem)
        {
            LoadoutItem = loadoutItem;
        }

        /// <inheritdoc/>
        public bool Equals(Installed other) => LoadoutItem.LoadoutItemId == other.LoadoutItem.LoadoutItemId;
        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is Installed other && Equals(other);
        /// <inheritdoc/>
        public override int GetHashCode() => LoadoutItem.Id.GetHashCode();
    }

    public bool IsNotDownloaded() => Value.IsT0;
    public bool IsDownloaded() => !IsNotDownloaded();
    public bool IsBundled() => Value.IsT1;

    public bool IsInLibrary(out LibraryItem.ReadOnly libraryItem)
    {
        if (!Value.TryPickT2(out var value, out _))
        {
            libraryItem = default(LibraryItem.ReadOnly);
            return false;
        }

        libraryItem = value.LibraryItem;
        return true;
    }

    public bool IsInstalled(out LoadoutItem.ReadOnly loadoutItem)
    {
        if (!Value.TryPickT3(out var value, out _))
        {
            loadoutItem = default(LoadoutItem.ReadOnly);
            return false;
        }

        loadoutItem = value.LoadoutItem;
        return true;
    }

    public static implicit operator CollectionDownloadStatus(NotDownloaded x) => new(x);
    public static implicit operator CollectionDownloadStatus(Bundled x) => new(x);
    public static implicit operator CollectionDownloadStatus(InLibrary x) => new(x);
    public static implicit operator CollectionDownloadStatus(Installed x) => new(x);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CollectionDownloadStatus other && Equals(other);

    /// <inheritdoc/>
    public bool Equals(CollectionDownloadStatus other)
    {
        var (index, otherIndex) = (Value.Index, other.Value.Index);
        if (index != otherIndex) return false;

        if (IsNotDownloaded()) return true;
        if (IsBundled()) return true;

        if (Value.TryPickT2(out var inLibrary, out _))
        {
            return inLibrary.Equals(other.Value.AsT2);
        }

        if (Value.TryPickT3(out var installed, out _))
        {
            return installed.Equals(other.Value.AsT3);
        }

        throw new UnreachableException();
    }

    /// <inheritdoc/>
    public override int GetHashCode() => Value.GetHashCode();
}

file class DatomEntityIdEqualityComparer : IEqualityComparer<Datom>
{
    public static readonly IEqualityComparer<Datom> Instance = new DatomEntityIdEqualityComparer();

    public bool Equals(Datom x, Datom y)
    {
        return x.E == y.E;
    }

    public int GetHashCode(Datom obj)
    {
        return obj.E.GetHashCode();
    }
}

internal class OptionalDatomComparer : IEqualityComparer<Optional<Datom>>
{
    public static readonly IEqualityComparer<Optional<Datom>> Instance = new OptionalDatomComparer();

    public bool Equals(Optional<Datom> x, Optional<Datom> y)
    {
        var (a, b) = (x.HasValue, y.HasValue);
        return (a, b) switch
        {
            (false, false) => true,
            (false, true) => false,
            (true, false) => false,
            (true, true) => x.Value.E.Equals(y.Value.E),
        };
    }

    public int GetHashCode(Optional<Datom> datom)
    {
        return datom.GetHashCode();
    }
}
