using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.HighPerformance.Buffers;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.GC;
using NexusMods.Abstractions.Loadouts.Files.Diff;
using NexusMods.Abstractions.Loadouts.Sorting;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.IndexSegments;
using NexusMods.MnemonicDB.Abstractions.Query;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using Reloaded.Memory.Extensions;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

using DiskState = Entities<DiskStateEntry.ReadOnly>;

/// <summary>
/// Base class for loadout synchronizers, provides some common functionality. Does not have to be user,
/// but reduces a lot of boilerplate, and is highly recommended.
/// </summary>
public partial class ALoadoutSynchronizer : ILoadoutSynchronizer
{
    /// <summary>
    /// We'll limit backups to 2GB, for now we should never see much more than this
    /// of modified game files. s
    /// </summary>
    private static Size MaximumBackupSize => Size.GB * 5;
    
    private readonly ScopedAsyncLock _lock = new();
    private readonly IFileStore _fileStore;
    private readonly IDownloadReExtractor _reExtractor;

    protected readonly ILogger Logger;
    private readonly IOSInformation _os;
    private readonly ISorter _sorter;
    private readonly IGarbageCollectorRunner _garbageCollectorRunner;
    private readonly ISynchronizerService _synchronizerService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoadoutManager _loadoutManager;
    private readonly IGameLocationsService _gameLocationsService;
    private readonly IGameRegistry _gameRegistry;

    private readonly StringPool _fileNamePool = new();

    /// <summary>
    /// Connection.
    /// </summary>
    protected readonly IConnection Connection;

    private readonly IJobMonitor _jobMonitor;
    private readonly IFileHashesService _fileHashService;

    /// <summary>
    /// Loadout synchronizer base constructor.
    /// </summary>
    protected ALoadoutSynchronizer(
        IServiceProvider serviceProvider,
        ILogger logger,
        IFileStore fileStore,
        ISorter sorter,
        IConnection conn,
        IOSInformation os,
        IFileHashesService fileHashService,
        IGarbageCollectorRunner garbageCollectorRunner)
    {
        _serviceProvider = serviceProvider;
        _synchronizerService = serviceProvider.GetRequiredService<ISynchronizerService>();
        _jobMonitor = serviceProvider.GetRequiredService<IJobMonitor>();
        _loadoutManager = serviceProvider.GetRequiredService<ILoadoutManager>();
        _gameLocationsService = serviceProvider.GetRequiredService<IGameLocationsService>();
        _gameRegistry = serviceProvider.GetRequiredService<IGameRegistry>();
        _reExtractor = serviceProvider.GetRequiredService<IDownloadReExtractor>();

        _fileHashService = fileHashService;

        Logger = logger;
        _fileStore = fileStore;
        _sorter = sorter;
        Connection = conn;
        _os = os;
        _garbageCollectorRunner = garbageCollectorRunner;
    }

    /// <summary>
    /// Helper constructor that takes only a service provider, and resolves the dependencies from it.
    /// </summary>
    /// <param name="provider"></param>
    protected ALoadoutSynchronizer(IServiceProvider provider) : this(
        provider,
        provider.GetRequiredService<ILogger<ALoadoutSynchronizer>>(),
        provider.GetRequiredService<IFileStore>(),
        provider.GetRequiredService<ISorter>(),
        provider.GetRequiredService<IConnection>(),
        provider.GetRequiredService<IOSInformation>(),
        provider.GetRequiredService<IFileHashesService>(),
        provider.GetRequiredService<IGarbageCollectorRunner>()
    ) { }

    private void CleanDirectories(IEnumerable<GamePath> directoriesWithDeletions, DiskState newDiskState, GameInstallation installation)
    {
        // Folders inside a whitelisted location (the Wine prefix) are the game's or Wine's, never ours to remove
        directoriesWithDeletions = directoriesWithDeletions.Where(dir => installation.Locations[dir.LocationId].ManagedFiles is null);
        var processedDirectories = new HashSet<GamePath>();
        var directoriesToDelete = new HashSet<GamePath>();
        var directoriesInUse = new HashSet<GamePath>();
        
        // Build set of directories that are in use (are ancestors of at least one file)
        foreach (var fileEntry in newDiskState)
        {
            var path = (GamePath)fileEntry.Path;
            var parent = path.Parent;
            var rootComponent = parent.GetRootComponent;
        
            // Add all parent directories to the set
            while (parent != rootComponent)
            {
                directoriesInUse.Add(parent);
                parent = parent.Parent;
            }
        }
        
        // Find all empty directories in the chain for each deletion
        foreach (var dirWithDeletion in directoriesWithDeletions)
        {
            var rootComponent = dirWithDeletion.GetRootComponent;
            
            var currentParentDir = dirWithDeletion;

            while (currentParentDir != rootComponent)
            {
                if (processedDirectories.Contains(currentParentDir))
                    break;
                
                // Check if directory contains files or is a parent of directories with files
                if (directoriesInUse.Contains(currentParentDir))
                    break;

                processedDirectories.Add(currentParentDir);
                directoriesToDelete.Add(currentParentDir);
                currentParentDir = currentParentDir.Parent;
            }
        }

        // Sort deepest-first so we delete leaf directories before their parents
        var sorted = directoriesToDelete
            .OrderByDescending(d => d.Path.Depth)
            .ToArray();

        foreach (var dir in sorted)
        {
            var absDir = installation.Locations.ToAbsolutePath(dir);
            if (!absDir.DirectoryExists()) continue;
            // Only delete if the directory is truly empty on disk (no files, no subdirectories).
            // Directories excluded from tracking (e.g. archive/pc/content) may exist as siblings
            // and would be destroyed by a recursive delete on the parent.
            if (absDir.EnumerateFiles().Any()) continue;
            if (absDir.EnumerateDirectories(recursive: false).Any()) continue;
            absDir.DeleteDirectory(recursive: false);
        }
    }

#region ILoadoutSynchronizer Implementation

    /// <summary>
    /// Gets or creates the override group.
    /// </summary>
    protected LoadoutOverridesGroupId GetOrCreateOverridesGroup(ITransaction tx, Loadout.ReadOnly loadout) =>
        LoadoutOverrides.GetOrCreate(tx, loadout);

    private class LoadoutItemGroupComparer : IEqualityComparer<LoadoutItemGroup.ReadOnly>, IAlternateEqualityComparer<EntityId, LoadoutItemGroup.ReadOnly>
    {
        public static readonly IEqualityComparer<LoadoutItemGroup.ReadOnly> Instance = new LoadoutItemGroupComparer();

        public bool Equals(LoadoutItemGroup.ReadOnly x, LoadoutItemGroup.ReadOnly y) => x.Id.Equals(y.Id);
        public int GetHashCode(LoadoutItemGroup.ReadOnly item) => item.Id.GetHashCode();
        public bool Equals(EntityId alternate, LoadoutItemGroup.ReadOnly other) => other.Id.Equals(alternate);
        public int GetHashCode(EntityId alternate) => alternate.GetHashCode();
        public LoadoutItemGroup.ReadOnly Create(EntityId alternate) => throw new NotSupportedException();
    }

    public Dictionary<GamePath, SyncNode> BuildSyncTree<T>(T latestDiskState, T previousDiskState, Loadout.ReadOnly loadout) where T : IEnumerable<PathPartPair>
    {
        Dictionary<GamePath, SyncNode> syncTree = new();

        var winningFiles = WinningFilesQuery(Connection.Db, loadout).ToList();
        Logger.LogDebug("[SYNC] WinningFiles query returned {Count} files (db tx={DbTx})", winningFiles.Count, Connection.Db.BasisTxId);

        // Log collection group states for debugging file count changes
        try
        {
            var collectionGroups = CollectionGroup.All(Connection.Db)
                .Where(cg => cg.AsLoadoutItemGroup().AsLoadoutItem().LoadoutId == loadout.LoadoutId)
                .ToList();
            foreach (var cg in collectionGroups)
            {
                var item = cg.AsLoadoutItemGroup().AsLoadoutItem();
                var isDisabled = item.IsDisabled;
                var childCount = Connection.Db.Datoms(LoadoutItem.Parent, item.Id).Count;
                Logger.LogDebug("[SYNC] Collection '{Name}' (id={Id}): disabled={Disabled}, children={Children}",
                    item.Name, item.Id, isDisabled, childCount);
            }
        }
        catch (Exception ex)
        {
            Logger.LogTrace(ex, "[SYNC] Could not enumerate collection groups");
        }

        foreach (var tuple in winningFiles)
        {
            var itemType = ToItemType(tuple.ItemType);

            // NOTE(erri120): deleted files are not added to the sync tree
            if (itemType == LoadoutSourceItemType.Deleted) continue;

            var gamePath = new GamePath(tuple.Location, tuple.Path);
            if (gamePath == default(GamePath)) throw new Exception($"Item of type `{itemType}` with ID `{tuple.Id}` has no valid game path!");

            ref var syncTreeEntry = ref CollectionsMarshal.GetValueRefOrAddDefault(syncTree, gamePath, out var exists);

            if (exists)
            {
                Logger.LogWarning("Duplicate file for `{Path}`: {Item}", gamePath, tuple);
                continue;
            }

            var loadoutPart = itemType switch
            {
                LoadoutSourceItemType.Game => new SyncNodePart
                {
                    Hash = tuple.Hash,
                    Size = tuple.Size,
                    LastModifiedTicks = 0,
                },
                LoadoutSourceItemType.Loadout => new SyncNodePart
                {
                    EntityId = tuple.Id,
                    Hash = tuple.Hash,
                    Size = tuple.Size,
                    LastModifiedTicks = 0,
                },
                LoadoutSourceItemType.Intrinsic => default(SyncNodePart),
                LoadoutSourceItemType.Deleted => throw new UnreachableException("Deleted files should've been filtered out"),
            };

            syncTreeEntry = new SyncNode
            {
                SourceItemType = itemType,
                Loadout = loadoutPart,
            };
        }

        MergeStates(latestDiskState, previousDiskState, syncTree);
        return syncTree;
    }

    /// <inheritdoc />
    public void MergeStates(IEnumerable<PathPartPair> currentState, IEnumerable<PathPartPair> previousTree, Dictionary<GamePath, SyncNode> loadoutItems)
    {
        foreach (var node in previousTree)
        {
            ref var existing = ref CollectionsMarshal.GetValueRefOrAddDefault(loadoutItems, node.Path, out var exists);
            if (exists)
            {
                existing.Previous = node.Part;
            }
            else
            {
                existing = new SyncNode
                {
                    Previous = node.Part,
                };
            }
        }
        
        foreach (var node in currentState)
        {
            ref var existing = ref CollectionsMarshal.GetValueRefOrAddDefault(loadoutItems, node.Path, out var exists);
            if (exists)
            {
                existing.Disk = node.Part;
            }
            else
            {
                existing = new SyncNode
                {
                    Disk = node.Part,
                };
            }
        }
    }

    /// <inheritdoc />
    public async Task<Dictionary<GamePath, SyncNode>> BuildSyncTree(Loadout.ReadOnly loadout)
    {
        var metadata = await ReindexState(loadout.InstallationInstance);

        var currentItems = GetDiskStateForGame(metadata);
        var prevItems = ((ILoadoutSynchronizer)this).GetPreviouslyAppliedDiskState(metadata);
        
        return BuildSyncTree(currentItems, prevItems, loadout);
    }

    /// <summary>
    /// This is a highly optimized way to load all the disk state for a game. It's a sorted merge
    /// join over all the required attributes for the results
    /// </summary>
    public unsafe List<PathPartPair> GetDiskStateForGame(Sdk.Games.GameInstallMetadata.ReadOnly metadata)
    {
        var db = metadata.Db;
        var pairs = new List<PathPartPair>();
        var mainAttrId = db.AttributeCache.GetAttributeId(DiskStateEntry.GameId.Id);
        var pathAttrId = db.AttributeCache.GetAttributeId(DiskStateEntry.Path.Id);
        var hashAttrId = db.AttributeCache.GetAttributeId(DiskStateEntry.Hash.Id);
        var sizeAttrId = db.AttributeCache.GetAttributeId(DiskStateEntry.Size.Id);
        var lastModifiedAttrId = db.AttributeCache.GetAttributeId(DiskStateEntry.LastModified.Id);
        
        // We start with a single reference iterator, that points to the game data we are trying to access
        // Since this data will return results sorted by E (entry Id) we can merge join to any other data 
        // that is sorted in the same order
        using var iterator = db.LightweightDatoms(SliceDescriptor.Create(mainAttrId, metadata));
        
        // Now we have iterators for each field to load
        using var pathIterator = db.LightweightDatoms(SliceDescriptor.Create(pathAttrId));
        using var hashIterator = db.LightweightDatoms(SliceDescriptor.Create(hashAttrId));
        using var sizeIterator = db.LightweightDatoms(SliceDescriptor.Create(sizeAttrId));
        using var lastModifiedIterator = db.LightweightDatoms(SliceDescriptor.Create(lastModifiedAttrId));
        
        // For each entry in the main iterator
        while (iterator.MoveNext())
        {
            // Fast-forward the other iterators to the same entry
            pathIterator.FastForwardTo(iterator.KeyPrefix.E);
            hashIterator.FastForwardTo(iterator.KeyPrefix.E);
            sizeIterator.FastForwardTo(iterator.KeyPrefix.E);
            lastModifiedIterator.FastForwardTo(iterator.KeyPrefix.E);
            
            // Get the location id for the path
            var locationId = MemoryMarshal.Read<LocationId>(pathIterator.ValueSpan.SliceFast(sizeof(EntityId)));
            var pathSpan = pathIterator.ValueSpan.SliceFast(sizeof(EntityId) + sizeof(LocationId));
            // The number of paths in a loadout don't often change much, so we'll put them all through a cache pool, which will
            // allow us to not have to create UTF16 strings on every load of the data
            var pathStr = _fileNamePool.GetOrAdd(pathSpan, Encoding.UTF8);
            var gamePath = new GamePath(locationId, RelativePath.CreateUnsafe(pathStr));

            var pathPartPair = new PathPartPair
            {
                Path = gamePath,
                Part = new SyncNodePart
                {
                    EntityId = iterator.KeyPrefix.E,
                    Hash = MemoryMarshal.Read<Hash>(hashIterator.ValueSpan),
                    Size = MemoryMarshal.Read<Size>(sizeIterator.ValueSpan),
                    LastModifiedTicks = MemoryMarshal.Read<long>(lastModifiedIterator.ValueSpan),
                },
            };
            pairs.Add(pathPartPair);
        }
        Logger.LogDebug("[SYNC] GetDiskStateForGame returned {Count} entries", pairs.Count);
        return pairs;
    }

    /// <summary>
    /// Converts Mnemonic db disk state entries to path part pairs.
    /// </summary>
    /// <param name="entries"></param>
    /// <returns></returns>
    private IEnumerable<PathPartPair> DiskStateToPathPartPair<T>(T entries) 
        where T : IEnumerable<DiskStateEntry.ReadOnly>
    {
         
        
        foreach (var entry in entries)
        {
            yield return new PathPartPair
            {
                Path = entry.Path,
                Part = new SyncNodePart
                {
                    EntityId = entry.Id,
                    Hash = entry.Hash,
                    Size = entry.Size,
                    LastModifiedTicks = entry.LastModified.UtcTicks,
                },
            };
        }
    }

    /// <inheritdoc />
    public virtual void ProcessSyncTree(Dictionary<GamePath, SyncNode> tree)
    {
        foreach (var path in tree.Keys)
        {
            // TODO: sucks that we have to do a lookup here, but we have no way to get the ref to the value otherwise
            ref var item = ref CollectionsMarshal.GetValueRefOrNullRef(tree, path);
            
            var signature = SignatureBuilder.Build(
                diskHash: item.HaveDisk ? item.Disk.Hash : Optional<Hash>.None,
                prevHash: item.HavePrevious ? item.Previous.Hash : Optional<Hash>.None,
                loadoutHash: item.HaveLoadout && item.Loadout.Hash != Hash.Zero ? item.Loadout.Hash : Optional<Hash>.None,
                diskArchived: item.HaveDisk && HaveArchive(item.Disk.Hash),
                prevArchived: item.HavePrevious && HaveArchive(item.Previous.Hash),
                loadoutArchived: item.Loadout.Hash != Hash.Zero && HaveArchive(item.Loadout.Hash),
                pathIsIgnored: IsIgnoredBackupPath(path),
                item.SourceItemType);

            item.Signature = signature;
            item.Actions = ActionMapping.MapActions(signature);
        }
    }

    /// <summary>
    /// Re-extracts, from the original downloads, any loadout file that still needs to be extracted to disk
    /// (i.e. not already deployed with a matching hash) whose hash is no longer in the file store.
    /// Must run before <see cref="ProcessSyncTree"/> builds signatures, otherwise a missing archive is
    /// reported as unable to extract instead of being scheduled for extraction.
    /// </summary>
    /// <remarks>
    /// Only considers <see cref="LoadoutSourceItemType.Loadout"/> nodes that are not already deployed
    /// (disk hash differs from, or is absent for, the loadout hash): already-deployed nodes map to
    /// <see cref="Actions.DoNothing"/> and don't need the archive, and game files are rarely archived
    /// in the store, so checking them here would restore nothing on every sync.
    /// </remarks>
    private async Task RestoreMissingArchives(Dictionary<GamePath, SyncNode> tree, SynchronizeLoadoutJob? job = null, CancellationToken ct = default)
    {
        var missing = tree.Values
            .Where(node => node.HaveLoadout
                && node.SourceItemType == LoadoutSourceItemType.Loadout
                && (!node.HaveDisk || node.Disk.Hash != node.Loadout.Hash)
                && !_fileStore.HaveFile(node.Loadout.Hash).Result)
            .Select(node => node.Loadout.Hash)
            .Distinct()
            .ToArray();
        if (missing.Length == 0) return;

        job?.SetStatus("Restaurando archivos faltantes desde Descargas");
        var restored = await _reExtractor.RestoreAsync(missing, ct);
        Logger.LogInformation("Faltaban {Missing} archivos en el store antes de sincronizar; {Restored} reextraídos desde Descargas", missing.Length, restored.Count);
    }

    /// <inheritdoc />
    public async Task<Loadout.ReadOnly> RunActions(Dictionary<GamePath, SyncNode> syncTree, Loadout.ReadOnly loadout, SynchronizeLoadoutJob? job = null)
    {
        using var _ = await _lock.LockAsync();
        using var tx = Connection.BeginTransaction();
        var gameMetadataId = loadout.InstallationId;
        var locations = loadout.InstallationInstance.Locations;
        EnsureDiskChangesStayInside(syncTree, locations);

        // Without the vanilla list every original game file looks like a leftover: deleting waits for the list
        if (syncTree.Values.Any(node => node.Actions.HasFlag(Actions.DeleteFromDisk)))
            EnsureVanillaDataKnown(Sdk.Games.GameInstallMetadata.Load(Connection.Db, gameMetadataId), "borrar archivos del juego");
        HashSet<GamePath> foldersWithDeletedFiles = [];
        EntityId? overridesGroup = null;

        foreach (var action in ActionsInOrder)
        {
            switch (action)
            {
                case Actions.DoNothing:
                    break;

                case Actions.BackupFile:
                    job?.SetStatus("Backing up files");
                    await ActionBackupNewFiles(loadout.InstallationInstance, loadout.InstallationId, syncTree);
                    break;

                case Actions.IngestFromDisk:
                    job?.SetStatus("Adding external changes");
                    ActionIngestFromDisk(syncTree, loadout, tx, ref overridesGroup);
                    break;
                
                case Actions.AdaptLoadout:
                    job?.SetStatus("Updating loadout");
                    await AdaptLoadout(syncTree, locations, loadout, tx, gameMetadataId);
                    break;

                case Actions.DeleteFromDisk:
                    job?.SetStatus("Deleting files");
                    ActionDeleteFromDisk(syncTree, locations, tx, gameMetadataId, foldersWithDeletedFiles, job);
                    break;

                case Actions.ExtractToDisk:
                    job?.SetStatus("Extracting files");
                    await ActionExtractToDisk(syncTree, locations, tx, gameMetadataId, job);
                    break;
                
                case Actions.WriteIntrinsic:
                    job?.SetStatus("Writing intrinsic files");
                    await ActionWriteIntrinsics(syncTree, locations, tx, loadout, gameMetadataId, job);
                    break;

                case Actions.AddReifiedDelete:
                    job?.SetStatus("Updating deleted files");
                    ActionAddReifiedDelete(syncTree, loadout, tx, ref overridesGroup);
                    break;

                case Actions.WarnOfUnableToExtract:
                    WarnOfUnableToExtract(syncTree);
                    break;

                case Actions.WarnOfConflict:
                    WarnOfConflict(syncTree);
                    break;
                
                default:
                    throw new InvalidOperationException($"Unknown action: {action}");
            }
        }

        job?.SetStatus("Recording changes");
        
        tx.Add(gameMetadataId, Sdk.Games.GameInstallMetadata.LastSyncedLoadout, loadout.Id);
        tx.Add(gameMetadataId, Sdk.Games.GameInstallMetadata.LastSyncedLoadoutTransaction, EntityId.From(tx.ThisTxId.Value));
        tx.Add(gameMetadataId, Sdk.Games.GameInstallMetadata.LastScannedDiskStateTransaction, EntityId.From(tx.ThisTxId.Value));
        tx.Add(loadout.Id, Loadout.LastAppliedDateTime, DateTime.UtcNow);
        await tx.Commit();

        loadout = loadout.Rebase();
        var newState = DiskStateEntry.FindByGame(loadout.Db, loadout.Installation);

        // Clean up empty directories
        if (foldersWithDeletedFiles.Count > 0)
        {
            CleanDirectories(foldersWithDeletedFiles, newState, loadout.InstallationInstance);
        }

        job?.SetStatus("Archive Cleanup");
        await _garbageCollectorRunner.RunAsync();


        return loadout;
    }

    private static void EnsureVanillaDataKnown(Sdk.Games.GameInstallMetadata.ReadOnly metadata, string operation)
    {
        if (GameBaselineFile.TryGetVanillaFiles(metadata, out _)) return;
        throw new InvalidOperationException(
            $"No se puede {operation}: todavía no hay lista de archivos originales para {metadata.Name}. " +
            "Sin esa lista tModManager borraría archivos del juego, así que no hace nada. " +
            "Podés rearmarla con el botón «Actualicé el juego» del juego en My Games.");
    }

    /// <summary>
    /// Throws before anything touches the disk when a write or delete would land outside its location: a <c>..</c>
    /// segment (<see cref="GameLocations.ToAbsolutePath"/> throws), a folder in between that is a symlink, or, in a
    /// location with a whitelist (the Wine prefix), a path that is not on the list or is itself a symlink.
    /// </summary>
    private static void EnsureDiskChangesStayInside(Dictionary<GamePath, SyncNode> syncTree, GameLocations locations)
    {
        const Actions diskChanges = Actions.DeleteFromDisk | Actions.ExtractToDisk | Actions.WriteIntrinsic | Actions.AdaptLoadout;
        foreach (var (path, node) in syncTree)
        {
            if ((node.Actions & diskChanges) == 0) continue;
            if (!locations.ContainsKey(path.LocationId))
                throw new InvalidOperationException($"La ubicación de `{path}` no está disponible en esta instalación (¿prefix borrado?); no se escribe ni se borra nada ahí hasta que vuelva");
            if (!locations.IsManaged(path))
                throw new InvalidOperationException($"`{path}` no está entre los archivos que tModManager gestiona en esa ubicación; no se escribe ni se borra nada ahí");
            var resolved = locations.ToAbsolutePath(path);
            if (SafePath.IsUnderSymlink(locations[path.LocationId].Path.ToString(), resolved.ToString()))
                throw new InvalidOperationException($"`{path}` está dentro de una carpeta que es un symlink; tModManager no escribe ni borra a través de links");
            if (locations[path.LocationId].ManagedFiles is not null && SafePath.IsSymlink(resolved))
                throw new InvalidOperationException($"`{path}` es un symlink; tModManager no escribe ni borra a través de links");
        }
    }

    private async Task ActionWriteIntrinsics(Dictionary<GamePath, SyncNode> syncTree, GameLocations gameLocations, IMainTransaction tx, Loadout.ReadOnly loadout, EntityId gameMetadataId, SynchronizeLoadoutJob? job)
    {
        var intrinsicFiles = IntrinsicFiles(loadout);
        foreach (var (path, node) in syncTree)
        {
            if (!node.Actions.HasFlag(Actions.WriteIntrinsic)) continue;
            if (node.SourceItemType != LoadoutSourceItemType.Intrinsic) throw new Exception("WriteIntrinsic should only be called on intrinsic files");

            var instance = intrinsicFiles[path];
            var resolvedPath = gameLocations.ToAbsolutePath(path);
            // The disk may hold data the loadout never saw (first apply after managing, when disk ==
            // previous state and nothing was ingested yet): always ingest first so the base is real, then
            // write only what Ingest says is missing. A blind Write here would wipe the game's own keys,
            // and with no file at all Ingest decides whether there is anything worth creating.
            ReadOnlyMemory<byte>? rewrite;
            if (resolvedPath.FileExists)
            {
                await using var stream = resolvedPath.Read();
                rewrite = await instance.Ingest(stream, loadout, syncTree, tx);
            }
            else
            {
                rewrite = await instance.Ingest(Stream.Null, loadout, syncTree, tx);
            }
            if (rewrite is { } bytes)
                WriteIntrinsicBytes(gameLocations, path, node, bytes.ToArray(), tx, gameMetadataId);
        }
    }

    private async Task AdaptLoadout(Dictionary<GamePath, SyncNode> syncTree, GameLocations gameLocations, Loadout.ReadOnly loadout, IMainTransaction tx, EntityId gameMetadataId)
    {
        var intrinsicFiles = IntrinsicFiles(loadout);
        foreach (var (path, node) in syncTree)
        {
            if (!node.Actions.HasFlag(Actions.AdaptLoadout)) continue;
            if (node.SourceItemType != LoadoutSourceItemType.Intrinsic) throw new Exception("AdaptLoadout should only be called on intrinsic files");

            var instance = intrinsicFiles[path];
            var resolvedPath = gameLocations.ToAbsolutePath(path);
            ReadOnlyMemory<byte>? rewrite;
            await using (var stream = resolvedPath.Read())
                rewrite = await instance.Ingest(stream, loadout, syncTree, tx);
            // Our keys land in the same apply instead of the next one
            if (rewrite is { } bytes)
                WriteIntrinsicBytes(gameLocations, path, node, bytes.ToArray(), tx, gameMetadataId);
        }
    }

    /// <summary>
    /// Writes a generated intrinsic file and records its disk state, like ActionExtractToDisk does, so the
    /// next sync compares against what was written instead of ingesting our own output as a game change.
    /// </summary>
    private static void WriteIntrinsicBytes(GameLocations gameLocations, GamePath path, SyncNode node, byte[] bytes, ITransaction tx, EntityId gameMetadataId)
    {
        var resolvedPath = gameLocations.ToAbsolutePath(path);
        resolvedPath.Parent.CreateDirectory();
        using (var stream = resolvedPath.Create())
        {
            stream.SetLength(0);
            stream.Write(bytes);
        }
        var hash = bytes.xxHash3();
        var size = Size.FromLong(bytes.Length);
        var writeTimeUtc = new DateTimeOffset(resolvedPath.FileInfo.LastWriteTimeUtc);
        if (node.HaveDisk)
        {
            var id = node.Disk.EntityId;
            tx.Add(id, DiskStateEntry.Hash, hash);
            tx.Add(id, DiskStateEntry.Size, size);
            tx.Add(id, DiskStateEntry.LastModified, writeTimeUtc);
        }
        else
        {
            _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
            {
                Path = path.ToGamePathParentTuple(gameMetadataId),
                Hash = hash,
                Size = size,
                LastModified = writeTimeUtc,
                GameId = gameMetadataId,
            };
        }
    }

    /// <summary>
    /// Updates the locator IDs on the loadout if the game has been updated by the store.
    /// This should be called before building the sync tree.
    /// </summary>
    private async ValueTask<Loadout.ReadOnly> UpdateLocatorIds(Loadout.ReadOnly loadout)
    {
        var locator = loadout.InstallationInstance.LocatorResult.Locator;
        if (!locator.TryLocate(loadout.Game, out var gameLocatorResult))
        {
            // NOTE(erri120): It would be very odd if we re-query the game, and it's not installed anymore
            Logger.LogCritical("Found no installation of the game `{Store}`/`{Game}` anymore!", loadout.Installation.Store, loadout.Game.DisplayName);
            return loadout;
        }

        var metadataLocatorIds = gameLocatorResult.LocatorIds;
        var newLocatorIds = metadataLocatorIds.Distinct().ToArray();

        if (newLocatorIds.Length != metadataLocatorIds.Length)
            Logger.LogWarning("Found duplicate locator IDs `{LocatorIds}` on gameLocatorResult for game `{Game}` while updating locator IDs", metadataLocatorIds, loadout.InstallationInstance.Game.DisplayName);

        var locatorsToAdd = newLocatorIds.Except(loadout.LocatorIds).ToArray();
        var locatorsToRemove = loadout.LocatorIds.Except(newLocatorIds).ToArray();

        // No reason to change the loadout if the version is the same
        if (locatorsToAdd.Length == 0 && locatorsToRemove.Length == 0)
            return loadout;

        if (Logger.IsEnabled(LogLevel.Information))
        {
            var sCurrent = loadout.LocatorIds.Select(x => x.Value).ToArray();
            var sToAdd = locatorsToAdd.Select(x => x.Value).ToArray();
            var sToRemove = locatorsToRemove.Select(x => x.Value).ToArray();
            Logger.LogInformation("Locator IDs changed Current=`{CurrentIds}` ToAdd=`{ToAdd}` ToRemove=`{ToRemove}`", sCurrent, sToAdd, sToRemove);
        }

        using var tx = Connection.BeginTransaction();

        if (_fileHashService.TryGetVanityVersion((gameLocatorResult.Store, newLocatorIds), out var vanityVersion))
        {
            tx.Add(loadout, Loadout.GameVersion, vanityVersion);
        }
        else
        {
            tx.Add(loadout, Loadout.GameVersion, VanityVersion.DefaultValue);
            Logger.LogWarning("Found no vanity version for locator IDs `{LocatorIds}` (`{Store}`)", newLocatorIds, gameLocatorResult.Store);
        }

        foreach (var id in locatorsToRemove)
            tx.Retract(loadout, Loadout.LocatorIds, id);
        foreach (var id in locatorsToAdd)
            tx.Add(loadout, Loadout.LocatorIds, id);

        // The vanilla list belongs to the old version: drop the marker with the IDs, so a rebuild that fails after this
        // commit leaves "no list" (deletions blocked, next sync rebuilds) instead of the old list passing for the new one
        if (Sdk.Games.GameInstallMetadata.BaselineFromDisk.TryGetValue(loadout.Installation, out var fromDisk))
            tx.Retract(loadout.InstallationId, Sdk.Games.GameInstallMetadata.BaselineFromDisk, fromDisk);

        var result = await tx.Commit();
        return loadout.Rebase(result.Db);
    }

    private async ValueTask<Loadout.ReadOnly> ReprocessOverrides(Loadout.ReadOnly loadout)
    {
        // A disk-made list (or none) is the only judge of what is original: the hash database may know some depots
        // the list lacks, and dropping an override there makes the file a leftover. The rebuild once the database
        // knows the whole version does this job instead
        if (!Sdk.Games.GameInstallMetadata.BaselineFromDisk.TryGetValue(loadout.Installation, out var fromDisk) || fromDisk)
            return loadout;

        // Make a lookup set of the new files based on current locator IDs
        var versionFiles = _fileHashService
            .GetGameFiles((loadout.Installation.Store, loadout.LocatorIds.ToArray()))
            .Select(file => file.Path)
            .ToHashSet();

        // Find all files in the overrides that match a path in the new files
        var toDelete = from grp in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemGroup().OfTypeLoadoutOverridesGroup()
            from item in grp.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath()
            let path = (GamePath)item.TargetPath
            where versionFiles.Contains(path)
            select item;

        // No files to process, return early
        if (!toDelete.Any())
            return loadout;

        using var tx = Connection.BeginTransaction();
        var gameMetadataId = loadout.InstallationId;

        // Delete all the matching override files
        foreach (var file in toDelete)
        {
            tx.Delete(file, recursive: false);

            // The backed up file is being 'promoted' to a game file, which needs
            // to be rooted explicitly in case the user uses a feature like 'undo'
            // to roll back a game version on a store (like Xbox/Epic) which does
            // not support downloading non-current version(s).
            if (!file.TryGetAsLoadoutFile(out var loadoutFile))
                continue;

            _ = new GameBackedUpFile.New(tx)
            {
                Hash = loadoutFile.Hash,
                GameInstallId = gameMetadataId,
            };
        }

        var result = await tx.Commit();
        return loadout.Rebase(result.Db);
    }

    /// <summary>
    /// Alternative to <see cref="RunActions"/> that ignores changes and optionally clears the last sync loadout metadata
    /// </summary>
    public async Task RunActions(Dictionary<GamePath, SyncNode> syncTree, GameInstallation gameInstallation)
    {
        using var _ = await _lock.LockAsync();
        using var tx = Connection.BeginTransaction();

        var metadata = _gameRegistry.ForceGetMetadata(gameInstallation);
        var locations = gameInstallation.Locations;
        EnsureDiskChangesStayInside(syncTree, locations);

        HashSet<GamePath> foldersWithDeletedFiles = [];

        foreach (var action in ActionsInOrder)
        {
            switch (action)
            {
                case Actions.DoNothing:
                    break;

                case Actions.BackupFile:
                    await ActionBackupNewFiles(gameInstallation, metadata, syncTree);
                    break;
                
                case Actions.AdaptLoadout:
                    if (ApplicationConstants.IsDebug && syncTree.Any(n => n.Value.Actions.HasFlag(Actions.AdaptLoadout)))
                        throw new InvalidOperationException("Cannot adapt loadout when not in a loadout context");
                    break;
                
                case Actions.WriteIntrinsic:
                    if (ApplicationConstants.IsDebug && syncTree.Any(n => n.Value.Actions.HasFlag(Actions.AdaptLoadout)))
                        throw new InvalidOperationException("Cannot adapt loadout when not in a loadout context");
                    break;

                case Actions.IngestFromDisk:
                    if (ApplicationConstants.IsDebug && syncTree.Any(n => n.Value.Actions.HasFlag(Actions.IngestFromDisk)))
                        throw new InvalidOperationException("Cannot ingest files from disk when not in a loadout context");
                    break;

                case Actions.DeleteFromDisk:
                    ActionDeleteFromDisk(syncTree, locations, tx, metadata, foldersWithDeletedFiles);
                    break;

                case Actions.ExtractToDisk:
                    await ActionExtractToDisk(syncTree, locations, tx, metadata);
                    break;

                case Actions.AddReifiedDelete:
                    if (ApplicationConstants.IsDebug && syncTree.Any(n => n.Value.Actions.HasFlag(Actions.AddReifiedDelete)))
                        throw new InvalidOperationException("Cannot add reified deletes when not in a loadout context");
                    break;

                case Actions.WarnOfUnableToExtract:
                    WarnOfUnableToExtract(syncTree);
                    break;

                case Actions.WarnOfConflict:
                    WarnOfConflict(syncTree);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown action: {action}");
            }
        }

        if (metadata.Contains(Sdk.Games.GameInstallMetadata.LastSyncedLoadout))
        {
            tx.Retract(metadata, Sdk.Games.GameInstallMetadata.LastSyncedLoadout, (EntityId)metadata.LastSyncedLoadout);
            tx.Retract(metadata, Sdk.Games.GameInstallMetadata.LastSyncedLoadoutTransaction, (EntityId)metadata.LastSyncedLoadoutTransaction);
        }

        tx.Add(metadata, Sdk.Games.GameInstallMetadata.LastScannedDiskStateTransaction, EntityId.From(tx.ThisTxId.Value));

        var result = await tx.Commit();

        var newMetadata = metadata.Rebase(result.Db);

        // Clean up empty directories
        if (foldersWithDeletedFiles.Count > 0)
        {
            CleanDirectories(foldersWithDeletedFiles, DiskStateEntry.FindByGame(newMetadata.Db, newMetadata), gameInstallation);
        }
    }

    private void WarnOfConflict(Dictionary<GamePath, SyncNode> tree)
    {
        
        foreach (var (path, node) in tree)
        {
            if (!node.Actions.HasFlag(Actions.WarnOfConflict))
                continue;
            Logger.LogWarning("Conflict in {Path}", path);
        }
    }

    private void WarnOfUnableToExtract(Dictionary<GamePath, SyncNode> groupings)
    {
        foreach (var (path, node) in groupings)
        {
            if (!node.Actions.HasFlag(Actions.WarnOfUnableToExtract))
                continue;
            Logger.LogWarning("Unable to extract {Path}", path);
        }
    }

    private void ActionAddReifiedDelete(Dictionary<GamePath, SyncNode> groupings, Loadout.ReadOnly loadout, ITransaction tx, ref EntityId? overridesGroup)
    {

        foreach (var (path, node) in groupings)
        {
            if (!node.Actions.HasFlag(Actions.AddReifiedDelete))
                continue;
            
            overridesGroup ??= GetOrCreateOverridesGroup(tx, loadout);
            
            // If this is not a new entity, we may have a matching file in the overrides group already
            if (!overridesGroup.Value.InPartition(PartitionId.Temp))
            {
                var group = LoadoutOverridesGroup.Load(loadout.Db, overridesGroup.Value);
                var foundMatch = group.AsLoadoutItemGroup().Children
                    .OfTypeLoadoutItemWithTargetPath()
                    .TryGetFirst(p => p.TargetPath == path, out var match);

                if (foundMatch)
                {

                    // A delete of a delete does nothing
                    if (match.TryGetAsDeletedFile(out var _))
                        continue;

                    // If we found a match, we need to remove the entity itself
                    tx.Delete(match, recursive: false);
                    continue;
                }
            }
                
            _ = new DeletedFile.New(tx, out var id)
            {
                Reason = "Reified delete",
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
                {
                    TargetPath = path.ToGamePathParentTuple(loadout.Id),
                    LoadoutItem = new LoadoutItem.New(tx, id)
                    {
                        Name = path.FileName,
                        ParentId = overridesGroup.Value,
                        LoadoutId = loadout.Id,
                    },
                },
            };
        }
    }

    private async Task ActionExtractToDisk(Dictionary<GamePath, SyncNode> groupings, GameLocations gameLocations, ITransaction tx, EntityId gameMetadataId, SynchronizeLoadoutJob? job = null)
    {
        List<(Hash Hash, AbsolutePath Path)> toExtract = [];
        
        foreach (var (path, node) in groupings)
        {
            if (!node.Actions.HasFlag(Actions.ExtractToDisk))
                continue;
            
            Debug.Assert(node.Loadout.Hash != Hash.Zero, "Loadout hash is zero, this should not happen");

            var resolvedPath = gameLocations.ToAbsolutePath(path);
            toExtract.Add((node.Loadout.Hash, resolvedPath));
        }

        // Extract files to disk
        Logger.LogDebug("Extracting {Count} files to disk", toExtract.Count);
        
        if (toExtract.Count > 0)
        {
            var (missing, restored) = await _reExtractor.RestoreMissingAsync(_fileStore, toExtract.Select(x => x.Hash), CancellationToken.None);
            if (missing > 0)
                Logger.LogInformation("Faltaban {Missing} archivos en el store; {Restored} reextraídos desde Descargas", missing, restored);

            await _fileStore.ExtractFiles(toExtract, CancellationToken.None, UpdateStatus);

            var isUnix = _os.IsUnix();
            foreach (var (gamePath, node) in groupings)
            {
                if (!node.Actions.HasFlag(Actions.ExtractToDisk))
                    continue;

                var resolvedPath = gameLocations.ToAbsolutePath(gamePath);
                var writeTimeUtc = new DateTimeOffset(resolvedPath.FileInfo.LastWriteTimeUtc);
                
                // Reuse the old disk state entry if it exists
                if (node.HaveDisk)
                {
                    var id = node.Disk.EntityId;
                    tx.Add(id, DiskStateEntry.Hash, node.Loadout.Hash);
                    tx.Add(id, DiskStateEntry.Size, node.Loadout.Size);
                    tx.Add(id, DiskStateEntry.LastModified, writeTimeUtc);
                }
                else
                {
                    _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
                    {
                        Path = gamePath.ToGamePathParentTuple(gameMetadataId),
                        Hash = node.Loadout.Hash,
                        Size = node.Loadout.Size,
                        LastModified = writeTimeUtc,
                        GameId = gameMetadataId,
                    };
                }


                // And mark them as executable if necessary, on Unix
                if (!isUnix)
                    continue;

                var ext = resolvedPath.Extension.ToString().ToLower();
                if (ext is not ("" or ".sh" or ".bin" or ".run" or ".py" or ".pl" or ".php" or ".rb" or ".out"
                    or ".elf")) continue;

                // Note (Sewer): I don't think we'd ever need anything other than just 'user' execute, but you can never
                // be sure. Just in case, I'll throw in group and other to match 'chmod +x' behaviour.
                var currentMode = resolvedPath.GetUnixFileMode();
                resolvedPath.SetUnixFileMode(currentMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }

        void UpdateStatus((int Current, int Max) progress)
        {
            var (current, max) = progress;
            job?.SetStatus($"({current}/{max}) Extracting files");
        }
    }

    private void ActionDeleteFromDisk(
        Dictionary<GamePath, SyncNode> groupings,
        GameLocations gameLocations,
        ITransaction tx,
        GameInstallMetadataId gameMetadataId,
        HashSet<GamePath> foldersWithDeletedFiles,
        SynchronizeLoadoutJob? job = null)
    {
        var itemIndex = 0;
        
        var deleteFileCount = groupings.Sum(static x => x.Value.Actions.HasFlag(Actions.DeleteFromDisk) ? 1 : 0);

        if (deleteFileCount > 0)
        {
            Logger.LogWarning("[SYNC] About to delete {Count} files from disk:", deleteFileCount);
            foreach (var (path, node) in groupings)
            {
                if (!node.Actions.HasFlag(Actions.DeleteFromDisk)) continue;
                Logger.LogDebug("[SYNC] DELETE {Path} | sig={Sig}", path, node.Signature);
            }
        }
        
        // Delete files from disk
        foreach (var (path, node) in groupings)
        {
            if (!node.Actions.HasFlag(Actions.DeleteFromDisk))
                continue;

            if (itemIndex % 1000f == 0)
                job?.SetStatus($"({itemIndex}/{deleteFileCount}) Deleting files");
            itemIndex++;

            var resolvedPath = gameLocations.ToAbsolutePath(path);
            resolvedPath.Delete();

            // Only delete the entry if we're not going to replace it
            if (!node.Actions.HasFlag(Actions.ExtractToDisk))
            {
                foldersWithDeletedFiles.Add(path.Parent);

                var id = node.Disk.EntityId;
                tx.Retract(id, DiskStateEntry.Path, ((EntityId)gameMetadataId, path.LocationId, path.Path));
                tx.Retract(id, DiskStateEntry.Hash, node.Disk.Hash);
                tx.Retract(id, DiskStateEntry.Size, node.Disk.Size);
                tx.Retract(id, DiskStateEntry.LastModified, new DateTimeOffset(node.Disk.LastModifiedTicks, TimeSpan.Zero));
                tx.Retract(id, DiskStateEntry.Game, (EntityId)gameMetadataId);
            }
        }
    }

    public record struct AddedEntry
    {
        public required LoadoutItem.New LoadoutItem { get; init; }
        public required LoadoutItemWithTargetPath.New LoadoutItemWithTargetPath { get; init; }
        public required LoadoutFile.New LoadoutFileEntry { get; init; }
    }

    private bool ActionIngestFromDisk(Dictionary<GamePath, SyncNode> syncTree, Loadout.ReadOnly loadout, ITransaction tx, ref EntityId? overridesGroupId)
    {
        overridesGroupId ??= GetOrCreateOverridesGroup(tx, loadout);
        var newGroup = true;
        LoadoutItemGroup.ReadOnly? overridesGroup = null;
        if (!overridesGroupId.Value.InPartition(PartitionId.Temp))
        {
            newGroup = false;
            overridesGroup = LoadoutItemGroup.Load(loadout.Db, overridesGroupId.Value);
        }

        var ingestedFiles = false;
        
        foreach (var (path, node) in syncTree)
        {
            if (!node.Actions.HasFlag(Actions.IngestFromDisk))
                continue;

            // If the overrides group is not new, we need to check if the file is already in the overrides group
            if (!newGroup)
            {
                var existingRecord = overridesGroup!.Value.Children
                    .OfTypeLoadoutItemWithTargetPath()
                    .FirstOrOptional(c => c.TargetPath == path);

                if (existingRecord.HasValue)
                {
                    // Update the disk entry
                    tx.Add(node.Disk.EntityId, DiskStateEntry.LastModified, new DateTimeOffset(node.Disk.LastModifiedTicks, TimeSpan.Zero));
                    
                    // Update the file entry
                    tx.Add(existingRecord.Value.Id, LoadoutFile.Hash, node.Disk.Hash);
                    tx.Add(existingRecord.Value.Id, LoadoutFile.Size, node.Disk.Size);
                    
                    // Mark that we ingested a file
                    ingestedFiles = true;
                    
                    // Skip the rest of this process
                    continue;
                }
            }

            // Entry was added
            var id = tx.TempId();
            var loadoutItem = new LoadoutItem.New(tx, id)
            {
                ParentId = overridesGroupId.Value,
                LoadoutId = loadout.Id,
                Name = path.FileName,
            };
            var loadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
            {
                LoadoutItem = loadoutItem,
                TargetPath = path.ToGamePathParentTuple(loadout.Id),
            };

            _ = new LoadoutFile.New(tx, id)
            {
                LoadoutItemWithTargetPath = loadoutItemWithTargetPath,
                Hash = node.Disk.Hash,
                Size = node.Disk.Size,
            };
            tx.Add(node.Disk.EntityId, DiskStateEntry.LastModified, new DateTimeOffset(node.Disk.LastModifiedTicks, TimeSpan.Zero));
            ingestedFiles = true;
        }

        return ingestedFiles;
    }

    /// <inheritdoc />
    public virtual async Task<Loadout.ReadOnly> Synchronize(Loadout.ReadOnly loadout, SynchronizeLoadoutJob? job = null)
    {
        loadout = loadout.Rebase();
        var previousLocatorIds = loadout.LocatorIds.ToHashSet();

        // Update locator IDs before building the sync tree
        loadout = await UpdateLocatorIds(loadout);

        // No list yet (new or pre-existing install), a new game version, or a list taken from the disk for a version the
        // hash database has learned since (ReprocessOverrides would drop External Changes the disk list doesn't hold)
        var installation = loadout.Installation;
        if (!Sdk.Games.GameInstallMetadata.BaselineFromDisk.TryGetValue(installation, out var fromDisk)
            || !previousLocatorIds.SetEquals(loadout.LocatorIds)
            || (fromDisk && NexusKnowsVersion(installation.Store, loadout.LocatorIds.Distinct().ToArray())))
        {
            await UpdateBaseline(loadout);
            loadout = loadout.Rebase();
        }

        // If we are swapping loadouts, then we need to synchronize the previous loadout first to ingest
        // any changes, then we can apply the new loadout.
        if (Sdk.Games.GameInstallMetadata.LastSyncedLoadout.TryGetValue(loadout.Installation, out var lastAppliedId) && lastAppliedId != loadout.Id)
        {
            var prevLoadout = Loadout.Load(loadout.Db, lastAppliedId);
            if (prevLoadout.IsValid())
            {
                await _loadoutManager.DeactivateCurrentLoadout(loadout.InstallationInstance);
                await _loadoutManager.ActivateLoadout(loadout);
                return loadout.Rebase();
            }
        }

        job?.SetStatus("Collecting files");
        var tree = await BuildSyncTree(loadout);
        await RestoreMissingArchives(tree, job);
        ProcessSyncTree(tree);
        loadout = await RunActions(tree, loadout, job);

        // Move any override files that now match game files after sync
        loadout = await ReprocessOverrides(loadout);
        return loadout;
    }

    public async Task<GameInstallMetadata.ReadOnly> RescanFiles(GameInstallation gameInstallation)
    {
        // Make sure the file hashes are up to date
        await _fileHashService.GetFileHashesDb();
        return await ReindexState(gameInstallation);
    }

    /// <summary>
    /// All actions, in execution order.
    /// </summary>
    private static readonly Actions[] ActionsInOrder = Enum.GetValues<Actions>().OrderBy(a => (ushort)a).ToArray();

    /// <summary>
    /// Returns true if the given hash has been archived.
    /// </summary>
    protected bool HaveArchive(Hash hash)
    {
        return _fileStore.HaveFile(hash).Result;
    }

    /// <summary>
    /// Returns true if the loadout state doesn't match the last scanned disk state.
    /// </summary>
    public bool ShouldSynchronize(Loadout.ReadOnly loadout, IEnumerable<PathPartPair> previousDiskState, IEnumerable<PathPartPair> lastScannedDiskState)
    {
        var syncTree = BuildSyncTree(lastScannedDiskState, previousDiskState, loadout);
        // Process the sync tree to get the actions populated in the nodes
        ProcessSyncTree(syncTree);
        
        return syncTree.Any(n => n.Value.Actions != Actions.DoNothing && n.Value.Actions != Actions.WarnOfUnableToExtract);
    }
    
    /// <inheritdoc />
    public FileDiffTree LoadoutToDiskDiff(Loadout.ReadOnly loadout, List<PathPartPair> previousDiskState, List<PathPartPair> lastScannedDiskState)
    {
        var syncTree = BuildSyncTree(lastScannedDiskState, previousDiskState, loadout);
        // Process the sync tree to get the actions populated in the nodes
        ProcessSyncTree(syncTree);

        List<KeyValuePair<GamePath, DiskDiffEntry>> diffs = [];

        foreach (var (path, node) in syncTree)
        {
            var syncNode = node;
            var actions = syncNode.Actions;
            DiskDiffEntry entry;
            
            if (actions.HasFlag(Actions.DoNothing))
            {
                entry = new DiskDiffEntry
                {
                    Hash = node.Loadout.Hash,
                    Size = node.Loadout.Size,
                    ChangeType = FileChangeType.None,
                    GamePath = path,
                };
                diffs.Add(KeyValuePair.Create(path, entry));
            }
            else if (actions.HasFlag(Actions.WarnOfUnableToExtract))
            {
                entry = new DiskDiffEntry
                {
                    Hash = node.Loadout.Hash,
                    Size = node.Loadout.Size,
                    ChangeType = FileChangeType.Added,
                    GamePath = path,
                };
            }
            else if (actions.HasFlag(Actions.ExtractToDisk))
            {
                entry = new DiskDiffEntry
                {
                    Hash = node.Loadout.Hash,
                    Size = node.Loadout.Size,
                    // If paired with a delete action, this is a modified file not a new one
                    ChangeType = actions.HasFlag(Actions.DeleteFromDisk) ? FileChangeType.Modified : FileChangeType.Added,
                    GamePath = path,
                };
            }
            else if (actions.HasFlag(Actions.DeleteFromDisk))
            {
                entry = new DiskDiffEntry
                {
                    Hash = node.Disk.Hash,
                    Size = node.Disk.Size,
                    ChangeType = FileChangeType.Removed,
                    GamePath = path,
                };
            }
            else if (actions.HasFlag(Actions.IngestFromDisk))
            {
                // File is already on disk and will not be changed
                entry = new DiskDiffEntry
                {
                    Hash = node.Disk.Hash,
                    Size = node.Disk.Size,
                    ChangeType = FileChangeType.None,
                    GamePath = path,
                };
            }
            else if (actions.HasFlag(Actions.AddReifiedDelete))
            {
                // File is not on disk and will not end up on disk, so don't show it
                continue;
            }
            else
            {
                // This really should become some sort of error state
                entry = new DiskDiffEntry
                {
                    Hash = Hash.Zero,
                    Size = Size.Zero,
                    ChangeType = FileChangeType.None,
                    GamePath = path,
                };
            }
            
            diffs.Add(KeyValuePair.Create(path, entry));
        }

        return FileDiffTree.Create(diffs);
    }

    /// <summary>
    /// Backs up any new files in the loadout.
    ///
    /// </summary>
    public virtual async Task ActionBackupNewFiles(GameInstallation installation, GameInstallMetadataId installMetadataId, Dictionary<GamePath, SyncNode> files)
    {
        // During ingest, new files that haven't been seen before are fed into the game's synchronizer to convert a
        // DiskStateEntry (hash, size, path) into some sort of LoadoutItem. By default, these are converted into a "LoadoutFile".
        // All Loadoutfile does, is say that this file is copied from the downloaded archives, that is, it's not generated
        // by any extension system.
        //
        // So the problem is, the ingest process has tagged all these new files as coming from the downloads, but likely
        // they've never actually been copied/compressed into the download folders. So if we need to restore them they won't exist.
        //
        // If a game wants other types of files to be backed up, they could do so with their own logic. But backing up a
        // IGeneratedFile is pointless, since when it comes time to restore that file we'll call file.Generate on it since
        // it's a generated file.

        // TODO: This may be slow for very large games when other games/mods already exist.
        // Backup the files that are new or changed
        var archivedFiles = new ConcurrentBag<ArchivedFileEntry>();
        var pinnedFileHashes = new ConcurrentBag<Hash>();
        await Parallel.ForEachAsync(files, async (item, _) =>
            {
                var (gamePath, node) = item;
                if (!node.Actions.HasFlag(Actions.BackupFile))
                    return;

                var path = installation.Locations.ToAbsolutePath(gamePath);
                Debug.Assert(node.HaveDisk, "Node must have a disk entry to backup");
                
                if (await _fileStore.HaveFile(node.Disk.Hash))
                    return;
                
                var archivedFile = new ArchivedFileEntry
                {
                    Size = node.Disk.Size,
                    Hash = node.Disk.Hash,
                    StreamFactory = new NativeFileStreamFactory(path),
                };

                archivedFiles.Add(archivedFile);
                
                // TODO: We should only pin game files, not override files as well.
                // This check does not work as intended because the winning files is going to be a Loadout one, not a game one.
                // if (node.SourceItemType == LoadoutSourceItemType.Game)
                pinnedFileHashes.Add(archivedFile.Hash);
            }
        );

        var totalSize = archivedFiles.Sum(static x => x.Size);
        if (totalSize > MaximumBackupSize)
        {
            var nodeSignatures = files
                .Where(static x => x.Value.Actions.HasFlag(Actions.BackupFile))
                .Select(static x => x.Value.Signature)
                .GroupBy(signature => signature)
                .Select(static grp => new {Signature = grp.Key, FileCount = grp.Count()})
                .ToList();
            
            Logger.LogError(
                """
                Cannot backup {FileCount} files with total size {TotalSize}, which exceeds maximum of {MaximumSize}. 
                Node signatures: 
                {Signatures}
                """,
                archivedFiles.Count,
                totalSize,
                MaximumBackupSize,
                string.Join(Environment.NewLine, nodeSignatures.Select(sig => $"  - {sig.Signature}: {sig.FileCount} files"))
            );
            
            throw new Exception($"Cannot backup files, total size is {totalSize}, which is larger than the maximum of {MaximumBackupSize}");
        }
        
        // PERFORMANCE: We deduplicate above with the HaveFile call.
        await _fileStore.BackupFiles(archivedFiles, deduplicate: false);

        // Pin the files to avoid garbage collection.
        using var tx = Connection.BeginTransaction();
        foreach (var hash in pinnedFileHashes)
        {
            _ = new GameBackedUpFile.New(tx)
            {
                GameInstallId = installMetadataId,
                Hash = hash,
            };
        }
        await tx.Commit();
    }

    /// <inheritdoc />
    public async Task<Sdk.Games.GameInstallMetadata.ReadOnly> UpdateBaseline(Loadout.ReadOnly loadout, bool adoptExternalChanges = false)
    {
        var metadata = await ReindexState(loadout.InstallationInstance);
        var store = metadata.Store;
        var locatorIds = loadout.LocatorIds.Distinct().ToArray();
        var nexusKnows = NexusKnowsVersion(store, locatorIds);

        var previous = new Dictionary<GamePath, (Hash Hash, Size Size)>();
        foreach (var entry in GameBaselineFile.FindByGame(metadata.Db, metadata))
            previous[entry.Path] = (entry.Hash, entry.Size);
        var disk = DiskStateEntry.FindByGame(metadata.Db, metadata).Select(e => ((GamePath)e.Path, e.Hash, e.Size)).ToList();
        // The disk holds the last synced loadout as it was applied: not `loadout` when switching loadouts, and
        // without edits made since (a mod disabled since then is still deployed and still the loadout's)
        var onDisk = loadout.Rebase();
        if (Sdk.Games.GameInstallMetadata.LastSyncedLoadout.TryGetValue(metadata, out var lastSyncedId)
            && metadata.Contains(Sdk.Games.GameInstallMetadata.LastSyncedLoadoutTransaction))
        {
            var appliedTx = Sdk.Games.GameInstallMetadata.LastSyncedLoadoutTransactionId.Get(metadata);
            var applied = Loadout.Load(metadata.Db.Connection.AsOf(TxId.From(appliedTx.Value)), lastSyncedId);
            if (applied.IsValid()) onDisk = applied;
        }
        var owned = OwnedPaths(onDisk);

        IEnumerable<(GamePath Path, Hash Hash, Size Size)> files;
        LoadoutFile.ReadOnly[] adopted = [];
        if (nexusKnows)
        {
            var nexus = new Dictionary<GamePath, (Hash Hash, Size Size)>();
            foreach (var f in _fileHashService.GetGameFiles((store, locatorIds)))
                nexus[f.Path] = (f.Hash, f.Size);

            // Nothing that was original becomes a leftover: entries Nexus doesn't list (a disk-made list's, files the
            // button adopted) stay while the disk still holds the path, with any content (an edit since the last sync
            // becomes an External Change against the kept original), or the loadout owns it (BaselineRule)
            var diskPaths = disk.Select(d => d.Item1).ToHashSet();
            foreach (var (path, entry) in previous)
            {
                if (!nexus.ContainsKey(path) && (owned.ContainsKey(path) || diskPaths.Contains(path)))
                    nexus[path] = entry;
            }

            // The hash DB only describes the game folder: every other location (the Wine prefix) follows the disk,
            // like a disk-made list does. Entries kept above win, so an edit since the last sync stays an External Change
            var outsideGame = disk.Where(d => d.Item1.LocationId != LocationId.Game).ToList();
            if (outsideGame.Count > 0)
            {
                var previousOutside = previous.Where(kv => kv.Key.LocationId != LocationId.Game).ToDictionary();
                foreach (var (path, entry) in BaselineRule.Apply(previousOutside, outsideGame, owned))
                    nexus.TryAdd(path, entry);
            }

            files = nexus.Select(kv => (kv.Key, kv.Value.Hash, kv.Value.Size));
        }
        else
        {
            if (adoptExternalChanges)
            {
                adopted = AdoptableExternalChanges(onDisk, owned, disk);
                foreach (var file in adopted)
                    owned.Remove(file.AsLoadoutItemWithTargetPath().TargetPath);
            }
            var fromDisk = BaselineRule.Apply(previous, disk, owned);

            // An empty or half-read folder (drive not mounted, game being deleted) would make every original a leftover
            var primaryFile = loadout.InstallationInstance.Game.GetPrimaryFile(loadout.InstallationInstance);
            if (!fromDisk.ContainsKey(primaryFile))
            {
                // The button reports the refusal (failure toast); automatic rebuilds only warn
                if (adoptExternalChanges)
                    throw new InvalidOperationException(
                        $"La lista de archivos originales de {loadout.InstallationInstance.Game.DisplayName} no se actualizó: " +
                        $"{primaryFile} no está en la carpeta del juego o lo pone un mod. Se mantiene la lista anterior.");
                Logger.LogWarning("No se rearmó la lista de archivos originales de {Game}: falta {PrimaryFile} en la carpeta del juego. Se mantiene la lista anterior",
                    loadout.InstallationInstance.Game.DisplayName, primaryFile);
                return metadata;
            }

            files = fromDisk.Select(kv => (kv.Key, kv.Value.Hash, kv.Value.Size));
        }

        using var tx = Connection.BeginTransaction();
        foreach (var old in GameBaselineFile.FindByGame(metadata.Db, metadata))
            tx.Delete(old, recursive: false);
        var count = 0;
        foreach (var (path, hash, size) in files)
        {
            _ = new GameBaselineFile.New(tx)
            {
                Path = path.ToGamePathParentTuple(metadata.Id),
                Hash = hash,
                Size = size,
                GameId = metadata.Id,
            };
            count++;
        }
        // False even with entries kept from the previous list: the "Nexus now knows" rebuild must not fire again, and
        // every later Nexus rebuild keeps them the same way
        tx.Add(metadata.Id, Sdk.Games.GameInstallMetadata.BaselineFromDisk, !nexusKnows);

        // Like ReprocessOverrides: the External Change is a game file now, and its backup stays rooted
        foreach (var file in adopted)
        {
            tx.Delete(file, recursive: false);
            _ = new GameBackedUpFile.New(tx)
            {
                Hash = file.Hash,
                GameInstallId = metadata.Id,
            };
        }
        await tx.Commit();

        Logger.LogInformation("Lista de archivos originales de {Game}: {Count} archivos, desde {Source}; {Adopted} cambios externos adoptados",
            loadout.InstallationInstance.Game.DisplayName, count, nexusKnows ? "la base de Nexus" : "el disco", adopted.Length);
        return Sdk.Games.GameInstallMetadata.Load(Connection.Db, metadata.Id);
    }

    /// <summary>
    /// External Changes the "Actualicé el juego" button turns into originals: overrides that are files (not deletions),
    /// were already External Changes when the loadout was applied, still match the disk, and sit where no mod has a file
    /// and the app generates nothing (a mod's file the game rewrote, like a config, is still the user's).
    /// </summary>
    private LoadoutFile.ReadOnly[] AdoptableExternalChanges(Loadout.ReadOnly onDisk, IReadOnlyDictionary<GamePath, Hash?> owned, IEnumerable<(GamePath Path, Hash Hash, Size Size)> disk)
    {
        // The current overrides, not the applied ones: only entities that still exist can be deleted
        var db = Connection.Db;
        var loadoutId = onDisk.LoadoutId;
        if (!LoadoutOverridesGroup.FindByOverridesFor(db, loadoutId).TryGetFirst(out var overrides)) return [];

        var diskHashes = new Dictionary<GamePath, Hash>();
        foreach (var (path, hash, _) in disk) diskHashes[path] = hash;

        var modPaths = LoadoutItem.FindByLoadout(db, loadoutId)
            .OfTypeLoadoutItemWithTargetPath()
            .Where(item => !item.AsLoadoutItem().HasParent() || item.AsLoadoutItem().ParentId.Value != overrides.Id)
            .Select(item => (GamePath)item.TargetPath)
            .ToHashSet();
        modPaths.UnionWith(IntrinsicFiles(onDisk).Keys);

        var adoptable = new List<LoadoutFile.ReadOnly>();
        foreach (var item in overrides.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
        {
            GamePath path = item.TargetPath;
            if (!item.TryGetAsLoadoutFile(out var file) || item.TryGetAsDeletedFile(out _)) continue;
            if (!owned.TryGetValue(path, out var ownedHash) || ownedHash is not null) continue;
            if (!diskHashes.TryGetValue(path, out var diskHash) || diskHash != file.Hash) continue;
            if (modPaths.Contains(path)) continue;
            adoptable.Add(file);
        }

        return adoptable.ToArray();
    }

    /// <summary>
    /// The Nexus hash database has the vanilla files of this version: Steam, and every locator ID known.
    /// </summary>
    private bool NexusKnowsVersion(GameStore store, LocatorId[] locatorIds) =>
        store == GameStore.Steam && _fileHashService.UnknownLocatorIds(store, locatorIds).Length == 0;

    /// <summary>
    /// Paths the loadout owns, for <see cref="BaselineRule"/>: mod files with their hash; External Changes, files
    /// deleted on purpose and files the app generates with null (their previous original is kept as is).
    /// </summary>
    private Dictionary<GamePath, Hash?> OwnedPaths(Loadout.ReadOnly loadout)
    {
        var owned = new Dictionary<GamePath, Hash?>();
        foreach (var row in WinningFilesQuery(loadout.Db, loadout))
        {
            var path = new GamePath(row.Location, row.Path);
            switch (ToItemType(row.ItemType))
            {
                case LoadoutSourceItemType.Loadout: owned[path] = row.Hash; break;
                case LoadoutSourceItemType.Deleted:
                case LoadoutSourceItemType.Intrinsic: owned[path] = null; break;
                case LoadoutSourceItemType.Game: break;
            }
        }

        foreach (var overrides in LoadoutOverridesGroup.FindByOverridesFor(loadout.Db, loadout.Id))
        {
            foreach (var item in overrides.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
                owned[item.TargetPath] = null;
        }

        return owned;
    }

    /// <summary>
    /// Reindex the state of the game, running a transaction if changes are found
    /// </summary>
    public async Task<GameInstallMetadata.ReadOnly> ReindexState(GameInstallation installation)
    {        
        using var _ = await _lock.LockAsync();

        var metadata = _gameRegistry.ForceGetMetadata(installation);
        using var tx = Connection.BeginTransaction();

        // Index the state
        var changed = await ReindexState(metadata, installation, tx);

        if (!metadata.Contains(Sdk.Games.GameInstallMetadata.InitialDiskStateTransaction))
        {
            // No initial state, so set this transaction as the initial state
            changed = true;
            tx.Add(metadata.Id, Sdk.Games.GameInstallMetadata.InitialDiskStateTransaction, EntityId.From(TxId.Tmp.Value));
        }

        if (changed)
        {
            tx.Add(metadata, Sdk.Games.GameInstallMetadata.LastScannedDiskStateTransactionId, EntityId.From(TxId.Tmp.Value));
            await tx.Commit();
        }

        return GameInstallMetadata.Load(Connection.Db, metadata);
    }

    /// <summary>
    /// A file that appears in a whitelisted location (the Wine prefix) without the app having put it there is the game's:
    /// nothing but the game writes there, and before this location existed the app never touched it (an install managed
    /// earlier, or a prefix created after managing, sees its settings for the first time here). It becomes an original
    /// right away, so no reset or unmanage deletes it, and it is backed up right away: unlike the game folder, no store
    /// or hash database can bring it back. Files a mod extracted are never new here: extraction records them.
    /// </summary>
    private async Task AdoptWhitelistedOriginals(GameInstallMetadata.ReadOnly metadata, GameInstallation installation, FrozenDictionary<GamePath, IndexFileResult> newFiles, ITransaction tx)
    {
        var candidates = newFiles
            .Where(kv => installation.Locations.TryGetValue(kv.Key.LocationId, out var location) && location.ManagedFiles is not null)
            .ToList();
        if (candidates.Count == 0) return;

        var baseline = GameBaselineFile.FindByGame(metadata.Db, metadata).Select(e => (GamePath)e.Path).ToHashSet();
        var adopted = candidates.Where(kv => !baseline.Contains(kv.Key)).ToList();
        if (adopted.Count == 0) return;

        var toBackup = new List<ArchivedFileEntry>();
        foreach (var (gamePath, result) in adopted)
        {
            if (await _fileStore.HaveFile(result.Hash)) continue;
            toBackup.Add(new ArchivedFileEntry
            {
                Hash = result.Hash,
                Size = result.Size,
                StreamFactory = new NativeFileStreamFactory(installation.Locations.ToAbsolutePath(gamePath)),
            });
        }
        if (toBackup.Count > 0)
            await _fileStore.BackupFiles(toBackup, deduplicate: false);

        foreach (var (gamePath, result) in adopted)
        {
            _ = new GameBaselineFile.New(tx)
            {
                Path = gamePath.ToGamePathParentTuple(metadata.Id),
                Hash = result.Hash,
                Size = result.Size,
                GameId = metadata.Id,
            };
            _ = new GameBackedUpFile.New(tx)
            {
                GameInstallId = metadata.Id,
                Hash = result.Hash,
            };
            Logger.LogInformation("`{Path}` apareció en una ubicación con whitelist sin que lo pusiera un mod: se toma como original y se respalda", gamePath);
        }
    }

    private FrozenDictionary<GamePath, DiskStateEntry.ReadOnly> GetDiskState(GameInstallMetadata.ReadOnly gameInstallMetadata)
    {
        var entities = DiskStateEntry.FindByGame(gameInstallMetadata.Db, gameInstallMetadata);
        var result = new Dictionary<GamePath, DiskStateEntry.ReadOnly>(capacity: entities.Count);

        foreach (var entity in entities)
        {
            GamePath gamePath = entity.Path;
            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(result, gamePath, out var isDuplicate);
            if (isDuplicate)
            {
                Logger.LogWarning("Duplicate path in disk state: `{Path}`", gamePath);
            }

            entry = entity;
        }

        return result.ToFrozenDictionary();
    }

    /// <summary>
    /// Reindex the state of the game
    /// </summary>
    private async Task<bool> ReindexState(GameInstallMetadata.ReadOnly metadata, GameInstallation installation, ITransaction tx)
    {
        var previousState = GetDiskState(metadata);

        var indexGameResult = await _gameLocationsService.IndexGame(
            installation: installation,
            previousDiskState: previousState,
            filter: GamePathFilter,
            cancellationToken: CancellationToken.None
        );

        await AdoptWhitelistedOriginals(metadata, installation, indexGameResult.NewFiles, tx);

        foreach (var (gamePath, result) in indexGameResult.NewFiles)
        {
            _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
            {
                Path = gamePath.ToGamePathParentTuple(metadata.Id),
                Hash = result.Hash,
                Size = result.Size,
                LastModified = result.LastModified,
                GameId = metadata.Id,
            };
        }

        foreach (var (gamePath, result) in indexGameResult.ModifiedFiles)
        {
            var didFind = previousState.TryGetValue(gamePath, out var previousDiskStateEntry);
            Debug.Assert(didFind, "modified file should be in previous state");

            tx.Add(previousDiskStateEntry.Id, DiskStateEntry.Size, result.Size);
            tx.Add(previousDiskStateEntry.Id, DiskStateEntry.Hash, result.Hash);
            tx.Add(previousDiskStateEntry.Id, DiskStateEntry.LastModified, result.LastModified);
        }

        foreach (var gamePath in indexGameResult.RemovedFiles)
        {
            var didFind = previousState.TryGetValue(gamePath, out var previousDiskStateEntry);
            Debug.Assert(didFind, "modified file should be in previous state");

            tx.Delete(previousDiskStateEntry.Id, recursive: false);
        }

        var hasChanged = indexGameResult.NewFiles.Count != 0 || indexGameResult.ModifiedFiles.Count != 0 || indexGameResult.RemovedFiles.Count != 0;
        if (!hasChanged) return false;

        tx.Add(metadata.Id, GameInstallMetadata.LastScannedDiskStateTransaction, EntityId.From(TxId.Tmp.Value));
        return true;
    }

    public async ValueTask BuildProcessRun(Loadout.ReadOnly loadout, GameInstallMetadata.ReadOnly state, CancellationToken cancellationToken)
    {
        var diskStateEntries = DiskStateEntry.FindByGame(state.Db, state);
        var tree = BuildSyncTree(DiskStateToPathPartPair(diskStateEntries), DiskStateToPathPartPair(diskStateEntries), loadout);
        await RestoreMissingArchives(tree, ct: cancellationToken);
        ProcessSyncTree(tree);
        await RunActions(tree, loadout);
    }

    /// <inheritdoc />
    public virtual bool IsIgnoredBackupPath(GamePath path) => false;

    /// <summary>
    /// Whether to ignore the file at the given path when indexing.
    /// </summary>
    /// <remarks>
    /// Files ignored by this method will not be included in the sync tree. Prefer not including
    /// the path in the first place instead of using this method.
    /// </remarks>
    protected virtual IGamePathFilter GamePathFilter { get; } = Synchronizers.GamePathFilters.Empty;

    /// <summary>
    /// Gets a set of files intrinsic to this game. Such as mod order files, preference files, etc.
    /// These files will not be backed up and will not be included in the loadout directly. Instead, they are
    /// generated at sync time by calling the implementations of the files themselves. 
    /// </summary>
    public virtual Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout)
    {
        return new();
    }

    public async Task ResetToOriginalGameState(GameInstallation installation)
    {
        var metadata = await ReindexState(installation);
        // The reset deletes everything that isn't in the vanilla list: with no list, that's the whole game
        EnsureVanillaDataKnown(metadata, "restaurar la carpeta del juego");
        GameBaselineFile.TryGetVanillaFiles(metadata, out var gameState);

        var diskStateEntries = DiskStateEntry.FindByGame(metadata.Db, metadata);

        List<PathPartPair> diskState = [];

        foreach (var diskFile in diskStateEntries)
        {
            diskState.Add(new PathPartPair(diskFile.Path, new SyncNodePart
            {   
                EntityId = diskFile.Id,
                Hash = diskFile.Hash,
                Size = diskFile.Size,
                LastModifiedTicks = diskFile.LastModified.UtcTicks,
            }));
        }

        Dictionary<GamePath, SyncNode> desiredState = new();

        foreach (var gameFile in gameState)
        {
            var part = new SyncNodePart
            {
                Hash = gameFile.Hash,
                Size = gameFile.Size,
                LastModifiedTicks = 0,
            };
            var syncNode = new SyncNode
            {
                Loadout = part,
                SourceItemType = LoadoutSourceItemType.Game,
            };
            desiredState.Add((GamePath)gameFile.Path, syncNode);
        }

        // Merge the states into a tree. Passing in the current state as the current and previous state. 
        // This fakes the synchronizer into thinking that there are no changes on disk and we only want to do a
        // hard reset to the desired state.
        MergeStates(diskState, diskState, desiredState);
        
        // Process the tree
        ProcessSyncTree(desiredState);

        // Run the groupings
        await RunActions(desiredState, installation);
    }
}

#endregion
