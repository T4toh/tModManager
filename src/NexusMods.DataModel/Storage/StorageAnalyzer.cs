using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.DataModel.LegacyData;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;

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
    }

    /// <summary>Test seam: overridden in tests so they never touch the real <c>~/.local/share</c>.</summary>
    internal Func<AbsolutePath> LegacyDownloadsFolderProvider { get; set; }

    private const string CyberpunkSteamAppId = "1091500";

    // keep in sync with CyberpunkDeepCleanTool.BackupsRoot — DataModel must not reference a game project
    private AbsolutePath GetCyberpunkBackupsPath() =>
        _fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME)
            .Combine(ApplicationConstants.DataDirectoryName)
            .Combine("Backups");

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
                .EnumerateFiles()
                .Aggregate(0UL, (acc, file) => acc + file.FileInfo.Size.Value);
        }

        // Sum sizes of all files under CyberpunkBackups (timestamped subdirs)
        var cyberpunkBackupsPath = GetCyberpunkBackupsPath();
        var cyberpunkBackupsSize = 0UL;
        if (cyberpunkBackupsPath.DirectoryExists())
        {
            cyberpunkBackupsSize = cyberpunkBackupsPath
                .EnumerateFiles(recursive: true)
                .Aggregate(0UL, (acc, file) => acc + file.FileInfo.Size.Value);
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
    public async Task RunDeepCleanOnAllLoadoutsAsync(CancellationToken cancellationToken = default)
    {
        var db = _connection.Db;
        var loadouts = Loadout.All(db).Where(l => l.IsVisible()).ToArray();
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
    public Task DeletePhysicalFilesAsync(CancellationToken cancellationToken = default)
    {
        // Delete all timestamped subdirectories under CyberpunkBackups
        var cyberpunkBackupsPath = GetCyberpunkBackupsPath();
        if (cyberpunkBackupsPath.DirectoryExists())
        {
            foreach (var subDir in cyberpunkBackupsPath.EnumerateDirectories())
                subDir.DeleteDirectory(recursive: true);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteDownloadsAsync(CancellationToken cancellationToken = default)
    {
        var downloads = _settingsManager.Get<DownloadsSettings>().Folder.ToPath(_fileSystem);
        if (downloads.DirectoryExists())
            foreach (var file in downloads.EnumerateFiles("*", recursive: false))
                file.Delete();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<(int Count, Size Size)> GetLegacyDownloadsAsync(CancellationToken cancellationToken = default)
    {
        var folder = LegacyDownloadsFolderProvider();
        if (!folder.DirectoryExists()) return Task.FromResult((0, Size.Zero));

        var files = folder.EnumerateFiles("*", recursive: false).ToArray();
        var size = files.Aggregate(0UL, (acc, file) => acc + file.FileInfo.Size.Value);
        return Task.FromResult((files.Length, Size.From(size)));
    }

    /// <inheritdoc />
    public async Task<int> MoveLegacyDownloadsAsync(CancellationToken cancellationToken = default)
    {
        var from = LegacyDownloadsFolderProvider();
        if (!from.DirectoryExists()) return 0;

        var to = _settingsManager.Get<DownloadsSettings>().Folder.ToPath(_fileSystem);
        to.CreateDirectory();

        var moved = 0;
        foreach (var file in from.EnumerateFiles("*", recursive: false).ToArray())
        {
            var name = DownloadsFolder.SanitizeFileName(file.FileName.ToString());
            var target = to.Combine(name);

            // Cheap path: rename directly when nothing is in the way. Only falls back to a copy
            // when the name clashes, or the move fails because source and destination are on
            // different filesystems (cross-device rename isn't atomic-renameable).
            if (!target.FileExists)
            {
                try
                {
                    File.Move(file.ToString(), target.ToString());
                    moved++;
                    continue;
                }
                catch (IOException)
                {
                    // Most likely source and destination are on different filesystems (rename can't
                    // cross a device boundary). Fall through to the copy+delete path below, which
                    // also correctly handles the rare race where something claimed `target` first.
                }
            }

            await DownloadsFolder.PlaceAsync(file, to, name, cancellationToken);
            file.Delete();
            moved++;
        }

        return moved;
    }

    /// <inheritdoc />
    public Task DeleteProtonPrefixAsync(AbsolutePath steamLibraryRoot, CancellationToken cancellationToken = default)
    {
        var steamApps = steamLibraryRoot.Combine("steamapps");
        if (!steamApps.DirectoryExists())
        {
            _logger.LogWarning("No se encontró steamapps en {Root}; no se borra el prefix de Proton", steamLibraryRoot);
            return Task.CompletedTask;
        }

        var prefix = steamApps.Combine($"compatdata/{CyberpunkSteamAppId}");
        if (!prefix.ToString().EndsWith($"steamapps/compatdata/{CyberpunkSteamAppId}", StringComparison.Ordinal))
        {
            _logger.LogWarning("Ruta de prefix inesperada {Prefix}; no se borra", prefix);
            return Task.CompletedTask;
        }

        if (prefix.DirectoryExists())
            prefix.DeleteDirectory(recursive: true);
        return Task.CompletedTask;
    }
}
