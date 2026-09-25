using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Collections;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using R3;
using NexusMods.Sdk.IO;

namespace NexusMods.Games.RedEngine.Cyberpunk2077;

public class CyberpunkDeepCleanTool : ITool
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<CyberpunkDeepCleanTool> _logger;
    private readonly ISynchronizerService _synchronizerService;
    private readonly IConnection _connection;
    private readonly IFileHashesService _fileHashes;

    public CyberpunkDeepCleanTool(
        IFileSystem fileSystem,
        ILogger<CyberpunkDeepCleanTool> logger,
        ISynchronizerService synchronizerService,
        IConnection connection,
        IFileHashesService fileHashes)
    {
        _fileSystem = fileSystem;
        _logger = logger;
        _synchronizerService = synchronizerService;
        _connection = connection;
        _fileHashes = fileHashes;
    }

    public IEnumerable<GameId> GameIds => [Cyberpunk2077Game.GameId];
    public string Name => "Deep Clean (Disable all mods)";

    // Paths to move to a timestamped backup directory (mirrors the bash script by manavortex).
    // IMPORTANT: Only mod-specific paths are included here. The original bash script also moves
    // engine/config/base, engine/config/galaxy, engine/config/platform/pc, r6/cache, and r6/config
    // — but those are base game files, restorable via Steam's "Verify game files".
    private static readonly string[] PathsToMove =
    [
        "archive/pc/mod",
        "mods",
        "bin/x64/plugins",
        "r6/scripts",
        "r6/tweaks",
        "red4ext",
        "engine/tools",
        "bin/x64/d3d11.dll",
        "bin/x64/global.ini",
        "bin/x64/powrprof.dll",
        "bin/x64/winmm.dll",
        "bin/x64/version.dll",
        "r6/audioware",
        "r6/input",
        "r6/config/cybercmd",
        "r6/config/redsUserHints",
        "r6/logs",
    ];

    private static readonly string[] PathsToDelete = ["V2077"];

    // Files mods drop outside their own folders. Moved only when the game's file list says they are not vanilla.
    // r6/publishing is a mixed directory: the base game ships r6/publishing/*/*/additional-content/addonDescriptions.xml
    // (confirmed against the Steam depot manifest), so unlike the other new mod directories it cannot be moved
    // wholesale via PathsToMove — every file under it must be checked against the vanilla set individually.
    private static readonly string[] LooseFileGlobs =
    [
        "*",                                   // game root, top level only
        "engine/config/platform/pc/*.ini",
        "engine/config/base/scripts.ini",
        "r6/cache/final.redscripts*",
        "r6/cache/input*.xml",
        "tools/redmod/tweaks/**/devices.tweak",
        "r6/publishing/**",
        "bin/x64/CyberPunk.bat",
    ];

    /// <summary>
    /// Finds files matching <see cref="LooseFileGlobs"/> under <paramref name="gameRoot"/> that are not
    /// part of the vanilla file set. Returns an empty list (never moves anything) when <paramref name="vanilla"/>
    /// is empty, since that means the file-hash service has no data for the installed game version.
    /// </summary>
    internal static IReadOnlyList<RelativePath> FindLooseModFiles(AbsolutePath gameRoot, IReadOnlySet<GamePath> vanilla)
    {
        if (vanilla.Count == 0)
            return [];

        // Vanilla paths come from Steam depot manifests (Windows casing); compare case-insensitively
        // so a vanilla file that differs only in case on the Linux disk is never treated as a leftover.
        var vanillaPaths = vanilla.Select(gp => gp.Path.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matcher = new Matcher();
        matcher.AddIncludePatterns(LooseFileGlobs);
        var root = gameRoot.ToString();
        var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(root)));
        return result.Files
            // Matcher descends into symlinked folders; a file reached through one lives outside the game folder
            .Where(f => !SafePath.IsUnderSymlink(root, Path.Combine(root, f.Path)))
            .Select(f => RelativePath.FromUnsanitizedInput(f.Path))
            .Where(rel => !vanillaPaths.Contains(rel.ToString()))
            .OrderBy(rel => rel.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Combines the vanilla files for every locator ID of the loadout. <paramref name="lookup"/> is called once
    /// per ID (typically <c>IFileHashesService.GetGameFiles</c> restricted to that single ID) so that an ID with
    /// no known manifest can be told apart from an ID that legitimately has no files.
    /// If ANY id resolves to zero files, the whole result is empty (with that id listed in <c>UnknownIds</c>):
    /// a partial vanilla set is worse than none, since it would treat some genuinely vanilla files (from the
    /// unresolved manifest) as mod leftovers. This happens after a game patch when the upstream hash database
    /// (discontinued) hasn't caught up yet for one of the game's locator IDs (e.g. the base depot manifest is
    /// unknown but REDmod's is known, or vice versa).
    /// </summary>
    internal static (IReadOnlySet<GamePath> Vanilla, IReadOnlyList<LocatorId> UnknownIds) ResolveVanilla(
        Func<LocatorId, IEnumerable<GamePath>> lookup, IReadOnlyList<LocatorId> ids)
    {
        var vanilla = new HashSet<GamePath>();
        var unknown = new List<LocatorId>();
        foreach (var id in ids)
        {
            var files = lookup(id).ToArray();
            if (files.Length == 0)
            {
                unknown.Add(id);
                continue;
            }
            foreach (var file in files)
                vanilla.Add(file);
        }

        return unknown.Count > 0 ? (new HashSet<GamePath>(), unknown) : (vanilla, unknown);
    }

    /// <summary>
    /// Root directory where Deep Clean backups are stored, outside the game folder.
    /// </summary>
    public static AbsolutePath BackupsRoot(IFileSystem fs) =>
        fs.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine(ApplicationConstants.DataDirectoryName).Combine("Backups");

    private void MoveToBackup(AbsolutePath from, AbsolutePath to, ref bool backupCreated, AbsolutePath backupDir)
    {
        if (!from.DirectoryExists() && !from.FileExists) return;

        if (!backupCreated)
        {
            backupDir.CreateDirectory();
            backupCreated = true;
        }

        if (!to.Parent.DirectoryExists())
            to.Parent.CreateDirectory();

        try
        {
            if (from.DirectoryExists())
                System.IO.Directory.Move(from.ToString(), to.ToString());
            else
                System.IO.File.Move(from.ToString(), to.ToString());

            _logger.LogInformation("Moved {Path} to backup", from);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move {Path} to backup", from);
        }
    }

    /// <summary>
    /// Deletes every backup folder under <paramref name="backupsRoot"/> except <paramref name="currentBackupName"/>
    /// and the newest one before it: a re-run (e.g. the cleanup wizard after a relaunch) that finds a few new
    /// leftovers must not delete the first, real backup of the user's mods.
    /// Does nothing unless <paramref name="backupCreated"/>: the older backup is then the only copy of the mod files.
    /// </summary>
    internal static void PruneOldBackups(AbsolutePath backupsRoot, string currentBackupName, bool backupCreated, ILogger logger)
    {
        if (!backupCreated) return;

        try
        {
            if (!backupsRoot.DirectoryExists()) return;

            var deletedBackups = 0;
            // Folder names are yyyyMMdd_HHmmss timestamps, so ordinal order is chronological.
            var olderBackups = System.IO.Directory.GetDirectories(backupsRoot.ToString())
                .Where(dir => System.IO.Path.GetFileName(dir) != currentBackupName)
                .OrderByDescending(dir => System.IO.Path.GetFileName(dir), StringComparer.Ordinal)
                .Skip(1);
            foreach (var oldBackupDir in olderBackups)
            {
                var dirName = System.IO.Path.GetFileName(oldBackupDir);
                try
                {
                    System.IO.Directory.Delete(oldBackupDir, true);
                    deletedBackups++;
                    logger.LogInformation("Deleted old backup: {Dir}", dirName);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete old backup: {Dir}", dirName);
                }
            }
            if (deletedBackups > 0)
                logger.LogInformation("Deleted {Count} old backup folder(s)", deletedBackups);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clean old backups");
        }
    }

    public async Task Execute(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        var gamePath = loadout.InstallationInstance.Locations[LocationId.Game].Path;
        _logger.LogInformation("Starting deep clean for Cyberpunk 2077 at {Path}", gamePath);

        // Step 1: Move mod files to a timestamped backup directory outside the game folder.
        // Keeping backups outside the game folder prevents the sync from tracking or trying to restore them.
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupsRoot = BackupsRoot(_fileSystem);
        var backupDir = backupsRoot.Combine(RelativePath.FromUnsanitizedInput(timestamp));
        var backupCreated = false;

        foreach (var relativePath in PathsToMove)
        {
            var rel = RelativePath.FromUnsanitizedInput(relativePath);
            MoveToBackup(gamePath.Combine(rel), backupDir.Combine(rel), ref backupCreated, backupDir);
        }

        IReadOnlySet<GamePath> vanilla = new HashSet<GamePath>();
        try
        {
            var resolved = ResolveVanilla(
                id => _fileHashes.GetGameFiles((loadout.Installation.Store, [id])).Select(f => f.Path),
                loadout.LocatorIds.ToArray());
            vanilla = resolved.Vanilla;

            if (resolved.UnknownIds.Count > 0)
            {
                _logger.LogWarning(
                    "No hay datos vanilla para los locator IDs {LocatorIds}; se omite la búsqueda de archivos sueltos de mods",
                    string.Join(", ", resolved.UnknownIds));
            }
            else if (vanilla.Count == 0)
            {
                _logger.LogWarning(
                    "No se encontraron datos de archivos vanilla para esta versión del juego; se omite la búsqueda de archivos sueltos de mods");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "No se pudo obtener la lista de archivos vanilla; se omite la búsqueda de archivos sueltos de mods");
        }

        foreach (var rel in FindLooseModFiles(gamePath, vanilla))
            MoveToBackup(gamePath.Combine(rel), backupDir.Combine(rel), ref backupCreated, backupDir);

        foreach (var relativePath in PathsToDelete)
        {
            var fullPath = gamePath.Combine(RelativePath.FromUnsanitizedInput(relativePath));
            try
            {
                if (fullPath.DirectoryExists())
                {
                    fullPath.DeleteDirectoryNoFollow();
                    _logger.LogInformation("Deleted directory {Path}", relativePath);
                }
                else if (fullPath.FileExists)
                {
                    fullPath.Delete();
                    _logger.LogInformation("Deleted file {Path}", relativePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete {Path}", relativePath);
            }
        }

        if (backupCreated)
            _logger.LogInformation("Mod files backed up to {Path}", backupDir);
        else
            _logger.LogInformation("No mod files found to back up");

        // Step 2: Delete previous backup folders created by earlier deep cleans (keeping the newest one before
        // this), but only when this run made a new one: a re-run that moved nothing must never delete the backup
        // holding the user's mod files.
        PruneOldBackups(backupsRoot, timestamp, backupCreated, _logger);

        // Step 3: Remove all mod groups and collections from the loadout database.
        // This ensures the app state is fully reset, not just disabled.
        // Library items and archives are preserved so mods can be re-installed without re-downloading.
        try
        {
            var db = _connection.Db;
            using var tx = _connection.BeginTransaction();
            var removedCount = 0;

            // Find all top-level LoadoutItemGroups (direct children of the loadout, not nested mod groups)
            // and delete them recursively. This removes:
            //   - Regular mod groups (LoadoutItemGroup)
            //   - Collection groups (NexusCollectionLoadoutGroup via CollectionGroup → LoadoutItemGroup)
            //   - Their nested mod groups and all LoadoutFiles within
            foreach (var item in LoadoutItem.FindByLoadout(db, loadout.Id).OfTypeLoadoutItemGroup())
            {
                // Only delete top-level groups (those directly under the Loadout, not nested under another group)
                var loadoutItem = item.AsLoadoutItem();
                if (loadoutItem.Contains(LoadoutItem.Parent)) continue; // skip nested groups

                // Skip the overrides group — it tracks vanilla game files that shouldn't be removed
                if (new[] { item }.OfTypeLoadoutOverridesGroup().Any()) continue;

                tx.Delete(item.Id, recursive: true);
                removedCount++;
            }

            if (removedCount > 0)
            {
                await tx.Commit();
                _logger.LogInformation("Removed {Count} top-level mod group(s) from the loadout database", removedCount);
            }
            else
            {
                _logger.LogInformation("No mod groups found to remove from the database");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove mods from the database");
        }

        // Step 4: Rescan the game folder so the app knows the disk state has changed.
        // This avoids the app trying to re-apply (or delete) files it no longer tracks.
        _logger.LogInformation("Rescanning game folder to update disk state...");
        await _synchronizerService.RescanFiles(loadout.InstallationInstance);
    }

    public IJobTask<ITool, Unit> StartJob(Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken cancellationToken)
    {
        return monitor.Begin<ITool, Unit>(this, async _ =>
        {
            await Execute(loadout, cancellationToken);
            return Unit.Default;
        });
    }
}
