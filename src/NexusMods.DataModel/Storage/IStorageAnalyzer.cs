using NexusMods.Paths;

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

    /// <summary>
    /// Cuenta y suma el tamaño de las descargas dejadas por el NexusMods.App original.
    /// </summary>
    Task<(int Count, Size Size)> GetLegacyDownloadsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Mueve las descargas dejadas por el NexusMods.App original a la carpeta de descargas actual.
    /// Mismo nombre y mismo contenido descarta el origen; mismo nombre y distinto contenido agrega
    /// un sufijo. Devuelve la cantidad de archivos movidos.
    /// </summary>
    Task<int> MoveLegacyDownloadsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Borra el prefix de Proton de Cyberpunk 2077 (<c>steamapps/compatdata/1091500</c>) bajo la
    /// biblioteca de Steam indicada. No hace nada si la ruta no coincide con lo esperado.
    /// </summary>
    Task DeleteProtonPrefixAsync(AbsolutePath steamLibraryRoot, CancellationToken cancellationToken = default);
}
