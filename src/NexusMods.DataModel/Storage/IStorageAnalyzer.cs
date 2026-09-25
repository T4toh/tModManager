namespace NexusMods.DataModel.Storage;

/// <summary>
/// Analyses disk usage for mod archives, game file backups, and downloaded files.
/// </summary>
public interface IStorageAnalyzer
{
    /// <summary>
    /// Computes current storage usage across archives, backups, and downloads.
    /// </summary>
    Task<StorageStats> GetStorageStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all <c>GameBackedUpFile</c> entries from the database, allowing the garbage
    /// collector to reclaim the corresponding archive chunks on the next GC run.
    /// </summary>
    Task DeleteAllBackedUpFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all mod groups from every visible loadout and runs the game-specific deep clean
    /// tool (moves mod files from the game folder to a timestamped backup and rescans).
    /// </summary>
    Task RunDeepCleanOnAllLoadoutsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all archive chunks from the store's content-addressed archive location.
    /// </summary>
    Task DeleteArchivesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all timestamped backup directories under CyberpunkBackups, freeing disk space
    /// occupied by mod-file snapshots created by the Deep Clean tool. Never touches the
    /// downloads folder.
    /// </summary>
    Task DeletePhysicalFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Borra las descargas originales. Solo por acción explícita del usuario.
    /// </summary>
    Task DeleteDownloadsAsync(CancellationToken cancellationToken = default);
}
