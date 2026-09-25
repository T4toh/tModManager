using NexusMods.Paths;

namespace NexusMods.DataModel.Storage;

/// <summary>
/// Aggregated disk usage statistics for the mod manager's data.
/// </summary>
public record StorageStats
{
    /// <summary>Total size on disk of all archive chunks in the content-addressed store.</summary>
    public Size ArchivesSize { get; init; }

    /// <summary>Number of original game files that have been backed up (GC roots).</summary>
    public int BackedUpFilesCount { get; init; }

    /// <summary>Total size on disk of all files in the downloads folder.</summary>
    public Size DownloadsFolderSize { get; init; }

    /// <summary>Total size on disk of timestamped mod backups created by Deep Clean.</summary>
    public Size CyberpunkBackupsSize { get; init; }
}
