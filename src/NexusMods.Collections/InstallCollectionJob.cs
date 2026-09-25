using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Collections;
using NexusMods.Abstractions.Collections.Json;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;

namespace NexusMods.Collections;

using ModAndDownload = (Mod Mod, CollectionDownload.ReadOnly Download);

/// <summary>
/// Job for installing a collection.
/// </summary>
public class InstallCollectionJob : IJobDefinitionWithStart<InstallCollectionJob, NexusCollectionLoadoutGroup.ReadOnly>
{ 
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public required NexusModsCollectionLibraryFile.ReadOnly SourceCollection { get; init; }
    public required CollectionRevisionMetadata.ReadOnly RevisionMetadata { get; init; }
    public required CollectionDownload.ReadOnly[] Items { get; init; }
    public required Optional<NexusCollectionLoadoutGroup.ReadOnly> Group { get; init; }

    public required IServiceProvider ServiceProvider { get; init; }
    public required IFileStore FileStore { get; init; }
    public required ILibraryService LibraryService { get; init; }
    public required ILoadoutManager LoadoutManager { get; init; }
    public required IConnection Connection { get; init; }
    public required LoadoutId TargetLoadout { get; init; }
    public required NexusModsLibrary NexusModsLibrary { get; init; }
    public required ILogger Logger { get; init; }
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member

    /// <summary>
    /// Factory.
    /// </summary>
    public static IJobTask<InstallCollectionJob, NexusCollectionLoadoutGroup.ReadOnly> Create(
        IServiceProvider provider,
        LoadoutId target,
        NexusModsCollectionLibraryFile.ReadOnly source,
        CollectionRevisionMetadata.ReadOnly revisionMetadata,
        CollectionDownload.ReadOnly[] items)
    {
        var connection = provider.GetRequiredService<IConnection>();
        var group = CollectionDownloader.GetCollectionGroup(revisionMetadata, target, connection.Db);

        var monitor = provider.GetRequiredService<IJobMonitor>();

        var job = new InstallCollectionJob
        {
            Group = group,
            Items = items,
            TargetLoadout = target,
            SourceCollection = source,
            RevisionMetadata = revisionMetadata,
            ServiceProvider = provider,
            Connection = connection,
            FileStore = provider.GetRequiredService<IFileStore>(),
            LibraryService = provider.GetRequiredService<ILibraryService>(),
            LoadoutManager = provider.GetRequiredService<ILoadoutManager>(),
            NexusModsLibrary = provider.GetRequiredService<NexusModsLibrary>(),
            Logger = provider.GetRequiredService<ILogger<InstallCollectionJob>>(),
        };

        return monitor.Begin<InstallCollectionJob, NexusCollectionLoadoutGroup.ReadOnly>(job);
    }

    /// <summary>
    /// Installs the collection.
    /// </summary>
    public async ValueTask<NexusCollectionLoadoutGroup.ReadOnly> StartAsync(IJobContext<InstallCollectionJob> context)
    {
        Logger.LogInformation("Starting installation of `{CollectionName}/{RevisionNumber}`", RevisionMetadata.Collection.Name, RevisionMetadata.RevisionNumber);

        var g = Group.Convert(static x => x.AsCollectionGroup());
        var items = Items
            .Where(item => !CollectionDownloader.GetStatus(item, g, Connection.Db).IsInstalled(out _))
            .ToArray();

        var skipCount = Items.Length - items.Length;
        if (skipCount > 0) Logger.LogInformation("Skipping `{Count}` already installed items for `{CollectionName}/{RevisionNumber}`", skipCount, RevisionMetadata.Collection.Name, RevisionMetadata.RevisionNumber);

        var isFullyDownloaded = CollectionDownloader.IsFullyDownloaded(items, db: Connection.Db);
        if (!isFullyDownloaded) throw new InvalidOperationException("The collection hasn't fully been downloaded!");

        // Check if the collection package archive is still on disk.
        // After a manual clean, the DB entry can survive but the downloaded package was deleted.
        // Also handle the case where SourceCollection is invalid (library file missing when page was opened).
        var downloads = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(ServiceProvider.GetRequiredService<IFileSystem>());
        var packageOnDisk = SourceCollection.IsValid()
            && LibraryFile.DownloadPath.TryGetValue(SourceCollection.AsLibraryFile(), out var packageRel)
            && downloads.Combine(packageRel).FileExists;

        if (packageOnDisk)
        {
            // The package itself is on disk, but its extracted entries (collection.json, bundled and
            // patch files) may still be missing from the file store after a manual clean — restore
            // them from the package before anything tries to read them from the store.
            var missingChildren = LibraryArchiveFileEntry.FindByParent(Connection.Db, SourceCollection.AsLibraryFile().Id)
                .Select(entry => entry.AsLibraryFile().Hash)
                .Where(hash => !FileStore.HaveFile(hash).Result)
                .Distinct()
                .ToArray();
            if (missingChildren.Length > 0)
            {
                var reExtractor = ServiceProvider.GetRequiredService<IDownloadReExtractor>();
                await reExtractor.RestoreAsync(missingChildren, context.CancellationToken);
            }

            // If collection.json is still missing after restoring, the package can't be parsed;
            // fall through to a fresh re-download like a fully-missing package.
            var jsonEntry = NexusModsLibrary.GetCollectionJsonFile(SourceCollection);
            if (!jsonEntry.IsValid() || !await FileStore.HaveFile(jsonEntry.AsLibraryFile().Hash))
                packageOnDisk = false;
        }

        NexusModsCollectionLibraryFile.ReadOnly sourceCollection = SourceCollection;
        if (!SourceCollection.IsValid() || !packageOnDisk)
        {
            Logger.LogWarning("Collection archive for '{Name}' is missing from disk. Removing stale entry and re-downloading automatically...", RevisionMetadata.Collection.Name);
            // Remove the stale DB entry if it exists
            if (SourceCollection.IsValid())
            {
                using var cleanupTx = Connection.BeginTransaction();
                cleanupTx.Delete(SourceCollection.Id, recursive: true);
                await cleanupTx.Commit();
            }

            // Re-download the collection package transparently
            var tempFileManager = ServiceProvider.GetRequiredService<TemporaryFileManager>();
            await using var destination = tempFileManager.CreateFile();
            var downloadJob = NexusModsLibrary.CreateCollectionDownloadJob(destination, RevisionMetadata.Collection.Slug, RevisionMetadata.RevisionNumber, context.CancellationToken);
            var libraryItem = await LibraryService.AddDownload(downloadJob);
            if (!libraryItem.TryGetAsNexusModsCollectionLibraryFile(out sourceCollection))
                throw new InvalidOperationException("Re-downloaded collection package is not a NexusModsCollectionLibraryFile");

            Logger.LogInformation("Collection package for '{Name}' re-downloaded successfully.", RevisionMetadata.Collection.Name);
        }

        var root = await NexusModsLibrary.ParseCollectionJsonFile(sourceCollection, context.CancellationToken);
        var modsAndDownloads = GatherDownloads(items, root);

        NexusCollectionLoadoutGroup.ReadOnly collectionGroup;
        // Re-check with the latest DB snapshot to prevent creating a duplicate group when
        // Group was resolved from a stale DB at job construction time (e.g., required install
        // just committed but connection.Db hadn't updated yet).
        var latestGroup = Group.HasValue
            ? Group
            : CollectionDownloader.GetCollectionGroup(RevisionMetadata, TargetLoadout, Connection.Db);

        if (latestGroup.HasValue)
        {
            collectionGroup = latestGroup.Value;
        }
        else
        {
            using var tx = Connection.BeginTransaction() ;
            var group = new NexusCollectionLoadoutGroup.New(tx, out var id)
            {
                CollectionId = RevisionMetadata.Collection,
                RevisionId = RevisionMetadata,
                LibraryFileId = sourceCollection,
                CollectionGroup = new CollectionGroup.New(tx, id)
                {
                    IsReadOnly = true,
                    LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                    {
                        IsGroup = true,
                        LoadoutItem = new LoadoutItem.New(tx, id)
                        {
                            Name = RevisionMetadata.Collection.Name,
                            LoadoutId = TargetLoadout,
                            IsDisabled = true,
                        },
                    },
                },
            };

            var groupResult = await tx.Commit();
            collectionGroup = groupResult.Remap(group);
        }

        var wasGroupCreatedByThisJob = !latestGroup.HasValue;
        try
        {
        var loadout = Loadout.Load(Connection.Db, TargetLoadout);
        var game = (loadout.InstallationInstance.Game as IGame)!;
        var fallbackInstaller = FallbackCollectionDownloadInstaller.Create(ServiceProvider, loadout, game);

        foreach (var modAndDownload in modsAndDownloads)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            try
            {
                Logger.LogDebug("Installing `{DownloadName}` (index={Index}) into `{CollectionName}/{RevisionNumber}`", modAndDownload.Mod.Name, modAndDownload.Download.ArrayIndex, RevisionMetadata.Collection.Name, RevisionMetadata.RevisionNumber);
                await InstallMod(modAndDownload, collectionGroup, fallbackInstaller, game.GetFallbackCollectionInstallDirectory(loadout.InstallationInstance), sourceCollection);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Failed to install `{DownloadName}` (index={Index}) into `{CollectionName}/{RevisionNumber}`", modAndDownload.Mod.Name, modAndDownload.Download.ArrayIndex, RevisionMetadata.Collection.Name, RevisionMetadata.RevisionNumber);
            }
        }

        var allRequiredItems = CollectionDownloader.GetItems(RevisionMetadata, CollectionDownloader.ItemType.Required);

        // Check via loadout DB instead of library status (more robust: library items may have been cleaned)
        // The group is enabled if it has ANY child items installed in the loadout.
        var hasAnyInstalledItems = Connection.Db.Datoms(LoadoutItem.Parent, collectionGroup.Id).Any();

        // Fall back to library status check for required items if there are no direct children yet
        var anyRequiredItemInstalled = hasAnyInstalledItems || allRequiredItems.Any(item => CollectionDownloader
            .GetStatus(item, collectionGroup.AsCollectionGroup(), db: Connection.Db)
            .IsInstalled(out _));

        Logger.LogDebug("[INSTALL-COLLECTION] hasAnyInstalledItems={HasAny}, anyRequiredItemInstalled={AnyRequired} → group will be {State}",
            hasAnyInstalledItems, anyRequiredItemInstalled, anyRequiredItemInstalled ? "ENABLED" : "DISABLED");
        {
            Logger.LogInformation("[INSTALL-COLLECTION] Applying collection download rules for '{CollectionName}' (groupId={GroupId}, loadoutId={LoadoutId})",
                RevisionMetadata.Collection.Name, collectionGroup.Id, TargetLoadout);
            await LoadoutManager.ApplyCollectionDownloadRules(collectionGroup, TargetLoadout);
            Logger.LogInformation("[INSTALL-COLLECTION] Collection download rules applied. Current DB tx={DbTx}",
                Connection.Db.BasisTxId);

            using var tx = Connection.BeginTransaction();

            // Enable group as soon as at least one item is installed (partial install is valid).
            // Only keep disabled if nothing is installed yet.
            if (anyRequiredItemInstalled)
            {
                tx.Retract(collectionGroup.Id, LoadoutItem.Disabled, Null.Instance);
            }
            else
            {
                tx.Add(collectionGroup.Id, LoadoutItem.Disabled, Null.Instance);
            }

            var result = await tx.Commit();
            Logger.LogInformation("[INSTALL-COLLECTION] Enable/disable committed for '{CollectionName}'. TX={TxId}, IsDisabled={IsDisabled}",
                RevisionMetadata.Collection.Name, result.Db.BasisTxId, !anyRequiredItemInstalled);
            collectionGroup = NexusCollectionLoadoutGroup.Load(result.Db, collectionGroup.Id);
        }

        return collectionGroup;
        }
        catch (Exception ex) when (wasGroupCreatedByThisJob)
        {
            Logger.LogError(ex, "Installation of `{CollectionName}/{RevisionNumber}` failed, rolling back created collection group", RevisionMetadata.Collection.Name, RevisionMetadata.RevisionNumber);
            using var cleanupTx = Connection.BeginTransaction();
            var groupDatoms = Connection.Db.Datoms(NexusCollectionLoadoutGroup.Revision, RevisionMetadata);
            foreach (var datom in groupDatoms)
                cleanupTx.Delete(datom.E, recursive: true);
            await cleanupTx.Commit();
            throw;
        }
    }

    private IJobTask<InstallCollectionDownloadJob, LoadoutItemGroup.ReadOnly> InstallMod(
        ModAndDownload modAndDownload,
        NexusCollectionLoadoutGroup.ReadOnly collectionGroup,
        ILibraryItemInstaller? fallbackInstaller,
        Optional<GamePath> fallbackCollectionInstallDirectory,
        NexusModsCollectionLibraryFile.ReadOnly sourceCollection)
    {
        var monitor = ServiceProvider.GetRequiredService<IJobMonitor>();

        var job = new InstallCollectionDownloadJob
        {
            Logger = ServiceProvider.GetRequiredService<ILogger<InstallCollectionJob>>(),
            Item = modAndDownload.Download,
            CollectionMod = modAndDownload.Mod,
            Group = collectionGroup.AsCollectionGroup(),
            TargetLoadout = TargetLoadout,
            SourceCollection = sourceCollection,

            ServiceProvider = ServiceProvider,
            Connection = Connection,
            FileStore = FileStore,
            LibraryService = LibraryService,
            LoadoutManager = LoadoutManager,

            FallbackInstaller = fallbackInstaller,
            FallbackCollectionInstallDirectory = fallbackCollectionInstallDirectory,
        };

        return monitor.Begin<InstallCollectionDownloadJob, LoadoutItemGroup.ReadOnly>(job);
    }

    private static List<ModAndDownload> GatherDownloads(CollectionDownload.ReadOnly[] items, CollectionRoot root)
    {
        var map = items.ToDictionary(static download => download.ArrayIndex, static download => download);
        var list = new List<ModAndDownload>();

        foreach (var kv in map)
        {
            var (index, download) = kv;
            var mod = root.Mods[index];

            list.Add((mod, download));
        }

        return list;
    }
}
