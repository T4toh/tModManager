using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.DataModel.LegacyData;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;
using NexusMods.Sdk.IO;

namespace NexusMods.DataModel.Storage;

/// <summary>
/// Analyses disk usage for mod archives, game file backups, and downloaded files.
/// </summary>
internal class StorageAnalyzer : IStorageAnalyzer
{
    private readonly IConnection _connection;
    private readonly ISettingsManager _settingsManager;
    private readonly IFileSystem _fileSystem;
    private readonly IToolManager _toolManager;
    private readonly IJobMonitor _jobMonitor;
    private readonly ILogger<StorageAnalyzer> _logger;
    private readonly LooseFileStore _fileStore;

    public StorageAnalyzer(
        IConnection connection,
        ISettingsManager settingsManager,
        IFileSystem fileSystem,
        IToolManager toolManager,
        IJobMonitor jobMonitor,
        ILogger<StorageAnalyzer> logger,
        LooseFileStore fileStore)
    {
        _connection = connection;
        _settingsManager = settingsManager;
        _fileSystem = fileSystem;
        _toolManager = toolManager;
        _jobMonitor = jobMonitor;
        _logger = logger;
        _fileStore = fileStore;
        LegacyDownloadsFolderProvider = () => LegacyDataDetector.LegacyDownloadsFolder(fileSystem);
        BackupsFolderProvider = () => GameBackups.Root(fileSystem);
        DefaultDownloadsFolderProvider = () => DownloadsSettings.DefaultFolder.ToPath(fileSystem);
    }

    /// <summary>Test seam: overridden in tests so they never touch the real <c>~/.local/share</c>.</summary>
    internal Func<AbsolutePath> LegacyDownloadsFolderProvider { get; set; }

    /// <summary>Test seam, same purpose as <see cref="LegacyDownloadsFolderProvider"/>: Deep Clean's <c>Backups</c> folder.</summary>
    internal Func<AbsolutePath> BackupsFolderProvider { get; set; }

    /// <summary>Test seam: tModManager's own downloads folder (<see cref="DownloadsSettings.DefaultFolder"/>).</summary>
    internal Func<AbsolutePath> DefaultDownloadsFolderProvider { get; set; }

    /// <inheritdoc />
    public Task<StorageStats> GetStorageStatsAsync(CancellationToken cancellationToken = default)
    {
        // Sum sizes of the files the store owns (foreign files under the archive location are not counted)
        var archivesSize = _fileStore.TotalSize().Value;

        // Count backed-up game files currently pinned in the database
        var db = _connection.Db;
        var backedUpCount = GameBackedUpFile.All(db).Count();

        // Sum sizes of all files in the downloads folder
        var downloadsPath = _settingsManager.Get<DownloadsSettings>().Folder.ToPath(_fileSystem);
        var downloadsSize = 0UL;
        if (downloadsPath.DirectoryExists())
        {
            downloadsSize = downloadsPath
                .EnumerateFiles("*", recursive: false)
                .Aggregate(0UL, (acc, file) => acc + file.FileInfo.Size.Value);
        }

        // Sum sizes of all files under Backups (timestamped Deep Clean snapshots)
        var cyberpunkBackupsPath = BackupsFolderProvider();
        var cyberpunkBackupsSize = 0UL;
        if (cyberpunkBackupsPath.DirectoryExists())
        {
            cyberpunkBackupsSize = (ulong)new DirectoryInfo(cyberpunkBackupsPath.ToString())
                .EnumerateFiles("*", NoFollowDelete.RecurseWithoutSymlinks)
                .Sum(file => file.Length);
        }

        var stats = new StorageStats
        {
            ArchivesSize = Size.From(archivesSize),
            BackedUpFilesCount = backedUpCount,
            DownloadsFolderSize = Size.From(downloadsSize),
            CyberpunkBackupsSize = Size.From(cyberpunkBackupsSize),
        };

        return Task.FromResult(stats);
    }

    /// <inheritdoc />
    public Task RunDeepCleanOnAllLoadoutsAsync(CancellationToken cancellationToken = default) =>
        RunDeepClean(skipSync: false, cancellationToken);

    /// <inheritdoc />
    public Task RunDeepCleanWithoutSyncOnAllLoadoutsAsync(CancellationToken cancellationToken = default) =>
        RunDeepClean(skipSync: true, cancellationToken);

    private async Task RunDeepClean(bool skipSync, CancellationToken cancellationToken)
    {
        var db = _connection.Db;
        var loadouts = Loadout.All(db).Where(l => l.IsVisible()).ToArray();

        // ponytail: one loadout per game. Each synced run makes its own snapshot and prunes the game's older ones, so
        // with 3+ loadouts of one game the first snapshot (the only one holding unmanaged files) is deleted. Games
        // have separate backup folders, so different games don't collide. Keep all snapshots of a run before lifting this.
        var crowded = loadouts.GroupBy(loadout => loadout.InstallationInstance.Game.GameId).FirstOrDefault(game => game.Count() > 1);
        if (!skipSync && crowded is not null)
            throw new InvalidOperationException($"Super Clean con {crowded.Count()} loadouts de {crowded.First().InstallationInstance.Game.DisplayName} no es seguro todavía: borrá los loadouts que no uses y volvé a intentar.");

        foreach (var loadout in loadouts)
        {
            var tool = _toolManager.GetTools(loadout)
                .FirstOrDefault(t => t.Name == "Deep Clean (Disable all mods)");
            if (tool is null)
            {
                _logger.LogWarning("Deep Clean tool not found for loadout {Name}", loadout.Name);
                continue;
            }
            _logger.LogInformation("Running Deep Clean on loadout {Name}", loadout.Name);
            if (skipSync)
                await tool.StartJob(loadout, _jobMonitor, cancellationToken);
            else
                await _toolManager.RunTool(tool, loadout, _jobMonitor, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAllBackedUpFilesAsync(CancellationToken cancellationToken = default)
    {
        var db = _connection.Db;
        var backedUpFiles = GameBackedUpFile.All(db).ToArray();
        if (backedUpFiles.Length == 0)
            return;

        using var tx = _connection.BeginTransaction();
        foreach (var file in backedUpFiles)
            tx.Delete(file.Id, recursive: false);

        await tx.Commit();
    }

    /// <inheritdoc />
    public Task DeleteArchivesAsync(CancellationToken cancellationToken = default)
    {
        // Only deletes files the store owns; never touches foreign content that may share the
        // configured Storage Location (e.g. a user-picked shared folder), and locations other than
        // the store's own root are never written by it in the first place.
        _fileStore.DeleteAll();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeletePhysicalFilesAsync(bool keepNewest = false, CancellationToken cancellationToken = default)
    {
        var backups = BackupsFolderProvider();
        if (!backups.DirectoryExists()) return Task.CompletedTask;

        // One folder per game (GameBackups), so "keep the newest" means each game's newest
        foreach (var gameFolder in backups.EnumerateDirectories(recursive: false))
        {
            // A linked game folder would make the folders it points to look like snapshots
            if (new DirectoryInfo(gameFolder.ToString()).LinkTarget is not null) continue;

            // Snapshot folders are named yyyyMMdd_HHmmss, so ordinal order is chronological.
            var snapshots = gameFolder.EnumerateDirectories(recursive: false)
                .OrderByDescending(dir => dir.FileName.ToString(), StringComparer.Ordinal)
                .Skip(keepNewest ? 1 : 0);
            foreach (var snapshot in snapshots)
                snapshot.DeleteDirectoryNoFollow();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteDownloadsAsync(CancellationToken cancellationToken = default)
    {
        var downloads = _settingsManager.Get<DownloadsSettings>().Folder.ToPath(_fileSystem);
        if (!downloads.DirectoryExists()) return Task.CompletedTask;

        // tModManager's own folder holds nothing but downloads, recorded or not (a database reset forgets them all):
        // empty it. A folder the user picked (or one linked elsewhere, e.g. to ~/Downloads) may hold their own
        // files, so there only the downloads the library recorded go.
        var ownFolder = downloads == DefaultDownloadsFolderProvider() && new DirectoryInfo(downloads.ToString()).LinkTarget is null;
        var files = ownFolder
            ? downloads.EnumerateFiles("*", recursive: false)
            : LibraryFile.All(_connection.Db)
                .Where(libraryFile => LibraryFile.DownloadPath.TryGetValue(libraryFile, out _))
                .Select(libraryFile => downloads.Combine(LibraryFile.DownloadPath.Get(libraryFile)));

        foreach (var file in files.ToArray())
        {
            if (!SafePath.IsStrictlyInside(downloads, file) || !file.FileExists || IsPartialDownload(file)) continue;
            file.Delete();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// A file <see cref="DownloadsFolder.TryClaimAsync"/> left half-written (named
    /// <c>&lt;name&gt;.tmp-&lt;guid&gt;</c>) after an interrupted move/copy. Never counted or moved:
    /// it isn't a real download, and touching it could race an in-flight write.
    /// </summary>
    private static bool IsPartialDownload(AbsolutePath file) =>
        file.FileName.ToString().Contains(".tmp-", StringComparison.Ordinal);

    /// <inheritdoc />
    public Task<(int Count, Size Size)> GetLegacyDownloadsAsync(CancellationToken cancellationToken = default)
    {
        var folder = LegacyDownloadsFolderProvider();
        if (!folder.DirectoryExists()) return Task.FromResult((0, Size.Zero));

        // Nothing is "pending" if the legacy folder already IS the current downloads folder
        // (configured that way directly, or one is a symlink to the other): there's nothing to move.
        var to = _settingsManager.Get<DownloadsSettings>().Folder.ToPath(_fileSystem);
        if (RealPath.Resolve(folder) == RealPath.Resolve(to)) return Task.FromResult((0, Size.Zero));

        var files = folder.EnumerateFiles("*", recursive: false).Where(f => !IsPartialDownload(f)).ToArray();
        var size = files.Aggregate(0UL, (acc, file) => acc + file.FileInfo.Size.Value);
        return Task.FromResult((files.Length, Size.From(size)));
    }

    /// <inheritdoc />
    public async Task<int> CopyLegacyDownloadsAsync(CancellationToken cancellationToken = default)
    {
        var from = LegacyDownloadsFolderProvider();
        if (!from.DirectoryExists()) return 0;

        var to = _settingsManager.Get<DownloadsSettings>().Folder.ToPath(_fileSystem);

        // Same physical folder (configured directly to the same path, or one is a symlink to the other): nothing to copy
        if (RealPath.Resolve(from) == RealPath.Resolve(to))
        {
            _logger.LogWarning("La carpeta de descargas antigua y la actual son la misma ({Path}); no se copia nada", from);
            return 0;
        }

        // Copied, never moved: the official NexusMods.App may still use them, deleting them is the user's call.
        // PlaceAsync reuses a file with the same name and content, so a retry after a failure (e.g. no space) resumes.
        var copied = 0;
        // A name with backslashes is read back as separators and can alias a file outside `from`
        foreach (var file in from.EnumerateFiles("*", recursive: false).Where(f => !IsPartialDownload(f) && SafePath.IsStrictlyInside(from, f)).ToArray())
        {
            await DownloadsFolder.PlaceAsync(file, to, file.FileName.ToString(), cancellationToken);
            copied++;
        }

        return copied;
    }

    /// <inheritdoc />
    public Task DeleteProtonPrefixAsync(GameInstallation installation, CancellationToken cancellationToken = default)
    {
        var appId = installation.LocatorResult.StoreIdentifier;
        var winePrefix = installation.LocatorResult.LinuxCompatabilityDataProvider?.WinePrefixDirectoryPath;
        if (installation.LocatorResult.Store != GameStore.Steam || winePrefix is null)
        {
            _logger.LogWarning("{Game} no tiene prefix de Proton de Steam; no se borra nada", installation.Game.DisplayName);
            return Task.CompletedTask;
        }

        // The locator reports <lib>/steamapps/compatdata/<appid>/pfx; the whole compatdata/<appid> folder goes.
        var prefix = winePrefix.Value.Parent;
        if (appId.Length == 0 || !appId.All(char.IsAsciiDigit) ||
            !prefix.ToString().EndsWith($"steamapps/compatdata/{appId}", StringComparison.Ordinal))
        {
            _logger.LogWarning("Ruta de prefix inesperada {Prefix} para el appid {AppId}; no se borra", prefix, appId);
            return Task.CompletedTask;
        }

        if (prefix.DirectoryExists())
            prefix.DeleteDirectoryNoFollow();
        return Task.CompletedTask;
    }
}
