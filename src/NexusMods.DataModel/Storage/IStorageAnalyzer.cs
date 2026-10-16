using NexusMods.Sdk.Games;
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
    /// Same as <see cref="RunDeepCleanOnAllLoadoutsAsync"/> but runs the deep clean tool directly, without the
    /// apply/ingest synchronizations around it. For the legacy cleanup: those synchronizations would need the
    /// old <c>.nx</c> archives, which can no longer be read.
    /// </summary>
    Task RunDeepCleanWithoutSyncOnAllLoadoutsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all archive chunks from the store's content-addressed archive location.
    /// </summary>
    Task DeleteArchivesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the timestamped backup directories under <c>Backups/&lt;GameId&gt;/</c>, freeing disk space
    /// occupied by mod-file snapshots created by the Deep Clean tool. With <paramref name="keepNewest"/>
    /// each game's most recent snapshot (the one a Deep Clean just made) survives. Never touches the
    /// downloads folder.
    /// </summary>
    Task DeletePhysicalFilesAsync(bool keepNewest = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Borra las descargas originales. Solo por acción explícita del usuario.
    /// </summary>
    Task DeleteDownloadsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Cuenta y suma el tamaño de las descargas dejadas por el NexusMods.App original.
    /// </summary>
    Task<(int Count, Size Size)> GetLegacyDownloadsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Copia las descargas dejadas por el NexusMods.App original a la carpeta de descargas actual; los
    /// originales quedan (la app oficial puede seguir usándolos). Mismo nombre y mismo contenido reusa el
    /// archivo ya copiado; mismo nombre y distinto contenido agrega un sufijo. Devuelve la cantidad de archivos.
    /// </summary>
    Task<int> CopyLegacyDownloadsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Borra el prefix de Proton de <paramref name="installation"/> (<c>steamapps/compatdata/&lt;appid&gt;</c>, la
    /// ruta que encontró el locator de Steam). No hace nada si no es una instalación de Steam o si la ruta no
    /// coincide con lo esperado.
    /// </summary>
    Task DeleteProtonPrefixAsync(GameInstallation installation, CancellationToken cancellationToken = default);
}
