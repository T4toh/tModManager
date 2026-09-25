using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
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
    }

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
}
