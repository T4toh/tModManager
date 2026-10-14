using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Collections;
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
    private readonly ILogger<CyberpunkDeepCleanTool> _logger;
    private readonly ISynchronizerService _synchronizerService;
    private readonly IConnection _connection;

    public CyberpunkDeepCleanTool(
        IFileSystem fileSystem,
        ILogger<CyberpunkDeepCleanTool> logger,
        ISynchronizerService synchronizerService,
        IConnection connection)
    {
        _logger = logger;
        _synchronizerService = synchronizerService;
        _connection = connection;
        BackupsRootProvider = () => GameBackups.ForGame(fileSystem, Cyberpunk2077Game.GameId);
    }

    /// <summary>
    /// Directory where this game's Deep Clean backups are stored, outside the game folder. Test seam: overridden in
    /// tests so they never write to (or prune) the real backups under <c>~/.local/share</c>.
    /// </summary>
    internal Func<AbsolutePath> BackupsRootProvider { get; set; }

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
    /// is empty, since that means the installation has no vanilla list yet.
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
    /// The loose mod files Deep Clean moves: those not in the installation's vanilla list. Without a list nothing is
    /// moved, since every original file would look like a leftover.
    /// </summary>
    internal static IReadOnlyList<RelativePath> LooseModFilesToMove(AbsolutePath gameRoot, GameInstallMetadata.ReadOnly installation, ILogger logger)
    {
        if (GameBaselineFile.TryGetVanillaFiles(installation, out var files))
            return FindLooseModFiles(gameRoot, files.Select(f => (GamePath)f.Path).ToHashSet());

        logger.LogWarning("Todavía no hay lista de archivos originales; se omite la búsqueda de archivos sueltos de mods");
        return [];
    }

    /// <summary>
    /// Moves <paramref name="from"/> into the backup. Returns false only when the move failed (e.g. no space left or
    /// permission denied), so the caller can stop before forgetting mods that were not backed up.
    /// </summary>
    private bool MoveToBackup(AbsolutePath gameRoot, AbsolutePath from, AbsolutePath to, ref bool backupCreated, AbsolutePath backupDir)
    {
        if (!from.DirectoryExists() && !from.FileExists) return true;

        // Under a symlinked folder (e.g. `bin` linked elsewhere) the path lives outside the game: leave it alone
        if (SafePath.IsUnderSymlink(gameRoot.ToString(), from.ToString()))
        {
            _logger.LogWarning("Se omite {Path}: está dentro de una carpeta que es un symlink", from);
            return true;
        }

        if (!backupCreated)
        {
            backupDir.CreateDirectory();
            backupCreated = true;
        }

        if (!to.Parent.DirectoryExists())
            to.Parent.CreateDirectory();

        try
        {
            // The game and the backups can be on different disks or btrfs subvolumes, where a rename fails
            if (from.DirectoryExists())
                NoFollowMove.MoveDirectoryNoFollow(from.ToString(), to.ToString());
            else
                System.IO.File.Move(from.ToString(), to.ToString());

            _logger.LogInformation("Moved {Path} to backup", from);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move {Path} to backup", from);
            return false;
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
        var timestamp = DateTime.Now.ToString(GameBackups.SnapshotNameFormat);
        var backupsRoot = BackupsRootProvider();
        var backupDir = backupsRoot.Combine(RelativePath.FromUnsanitizedInput(timestamp));
        var backupCreated = false;

        var failedMoves = 0;
        foreach (var relativePath in PathsToMove)
        {
            var rel = RelativePath.FromUnsanitizedInput(relativePath);
            if (!MoveToBackup(gamePath, gamePath.Combine(rel), backupDir.Combine(rel), ref backupCreated, backupDir)) failedMoves++;
        }

        foreach (var rel in LooseModFilesToMove(gamePath, loadout.Installation, _logger))
        {
            if (!MoveToBackup(gamePath, gamePath.Combine(rel), backupDir.Combine(rel), ref backupCreated, backupDir)) failedMoves++;
        }

        // Removing the mods from the database below would make the next apply delete the files that could not be
        // backed up, and pruning could drop the only older backup. Stop here instead.
        if (failedMoves > 0)
            throw new InvalidOperationException($"No se pudieron mover {failedMoves} carpeta(s) o archivo(s) al backup (¿sin espacio o sin permisos?). Lo que sí se movió quedó en {backupDir}; no se borró nada y los mods siguen en la base, así que al aplicar se vuelven a poner. Revisá el log.");

        foreach (var relativePath in PathsToDelete)
        {
            var fullPath = gamePath.Combine(RelativePath.FromUnsanitizedInput(relativePath));
            if (SafePath.IsUnderSymlink(gamePath.ToString(), fullPath.ToString()))
            {
                _logger.LogWarning("Se omite {Path}: está dentro de una carpeta que es un symlink", relativePath);
                continue;
            }

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
            await RemoveModGroups(_connection, loadout.LoadoutId, _logger);
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

    /// <summary>
    /// Deletes every top-level group of the loadout (mods, Nexus collections and their nested groups and files),
    /// except the overrides group and the editable collections ("My Mods"), which are emptied instead: the library
    /// installs into an editable collection and needs one to exist.
    /// </summary>
    internal static async Task RemoveModGroups(IConnection connection, LoadoutId loadoutId, ILogger logger)
    {
        var db = connection.Db;
        using var tx = connection.BeginTransaction();
        var removedCount = 0;

        foreach (var item in LoadoutItem.FindByLoadout(db, loadoutId).OfTypeLoadoutItemGroup())
        {
            if (item.AsLoadoutItem().Contains(LoadoutItem.Parent)) continue; // nested groups go with their parent

            // The overrides group tracks vanilla game files that shouldn't be removed
            if (new[] { item }.OfTypeLoadoutOverridesGroup().Any()) continue;

            if (item.TryGetAsCollectionGroup(out var collection) && !collection.IsReadOnly)
            {
                foreach (var child in LoadoutItem.FindByParent(db, item.Id))
                {
                    tx.Delete(child.Id, recursive: true);
                    removedCount++;
                }
                continue;
            }

            tx.Delete(item.Id, recursive: true);
            removedCount++;
        }

        if (removedCount > 0)
        {
            await tx.Commit();
            logger.LogInformation("Removed {Count} mod group(s) from the loadout database", removedCount);
        }
        else
        {
            logger.LogInformation("No mod groups found to remove from the database");
        }
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
