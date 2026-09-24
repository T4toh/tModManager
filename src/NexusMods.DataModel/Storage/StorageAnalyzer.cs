using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Sdk.Jobs;
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

    public StorageAnalyzer(
        IConnection connection,
        ISettingsManager settingsManager,
        IFileSystem fileSystem,
        IToolManager toolManager,
        IJobMonitor jobMonitor,
        ILogger<StorageAnalyzer> logger)
    {
        _connection = connection;
        _settingsManager = settingsManager;
        _fileSystem = fileSystem;
        _toolManager = toolManager;
        _jobMonitor = jobMonitor;
        _logger = logger;
    }

    private AbsolutePath GetCyberpunkBackupsPath() =>
        _fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME)
            .Combine("NexusMods.App")
            .Combine("CyberpunkBackups");

    /// <inheritdoc />
    public Task<StorageStats> GetStorageStatsAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsManager.Get<DataModelSettings>();

        // Sum sizes of all files under the archive locations (loose content-addressed store)
        var archivesSize = settings.ArchiveLocations
            .Select(loc => loc.ToPath(_fileSystem))
            .Where(dir => dir.DirectoryExists())
            .SelectMany(dir => dir.EnumerateFiles("*", recursive: true))
            .Aggregate(0UL, (acc, file) => acc + file.FileInfo.Size.Value);

        // Count backed-up game files currently pinned in the database
        var db = _connection.Db;
        var backedUpCount = GameBackedUpFile.All(db).Count();

        // Sum sizes of all files in the downloads folder
        var downloadsPath = settings.DownloadsFolder.ToPath(_fileSystem);
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
        var settings = _settingsManager.Get<DataModelSettings>();
        foreach (var loc in settings.ArchiveLocations)
        {
            var dir = loc.ToPath(_fileSystem);
            if (!dir.DirectoryExists()) continue;
            foreach (var sub in dir.EnumerateDirectories())
                sub.DeleteDirectory(recursive: true);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeletePhysicalFilesAsync(CancellationToken cancellationToken = default)
    {
        // Delete all files in the downloads folder
        var settings = _settingsManager.Get<DataModelSettings>();
        var downloadsPath = settings.DownloadsFolder.ToPath(_fileSystem);
        if (downloadsPath.DirectoryExists())
        {
            foreach (var file in downloadsPath.EnumerateFiles())
                file.Delete();
        }

        // Delete all timestamped subdirectories under CyberpunkBackups
        var cyberpunkBackupsPath = GetCyberpunkBackupsPath();
        if (cyberpunkBackupsPath.DirectoryExists())
        {
            foreach (var subDir in cyberpunkBackupsPath.EnumerateDirectories())
                subDir.DeleteDirectory(recursive: true);
        }

        return Task.CompletedTask;
    }
}
