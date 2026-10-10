using Microsoft.Extensions.Logging;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;

namespace NexusMods.Library;

/// <summary>
/// One-time repair for local files registered before hand-added files were copied into Downloads:
/// copies each one whose original still exists with the same content and records its DownloadPath.
/// Idempotent; a run that stops halfway finishes on the next start. Started from Program.cs once the
/// database has been migrated, never as a hosted service (those start before the second-instance,
/// reset and migration guards, and resolving IConnection there would open the database too early).
/// </summary>
public sealed class LocalFileBackfill(IConnection connection, ISettingsManager settings, IFileSystem fileSystem, ILogger<LocalFileBackfill> logger)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var downloads = settings.Get<DownloadsSettings>().Folder.ToPath(fileSystem);
        var copied = 0;
        foreach (var local in LocalFile.All(connection.Db).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            var libraryFile = local.AsLibraryFile();
            if (LibraryFile.DownloadPath.TryGetValue(libraryFile, out _)) continue;
            // Collection downloads store a bare file name here; they always have a DownloadPath, but be safe.
            if (!Path.IsPathRooted(local.OriginalPath)) continue;
            var name = libraryFile.AsLibraryItem().Name;

            try
            {
                var original = fileSystem.FromUnsanitizedFullPath(local.OriginalPath);
                // File.Exists is false for directories and for links to directories.
                if (!original.FileExists)
                {
                    logger.LogInformation("Archivo local '{Name}' sin descarga: el original '{Path}' ya no está", name, local.OriginalPath);
                    continue;
                }
                // The user may have overwritten the original with a newer version since it was added;
                // adopting that would make re-extraction fail with a hash mismatch instead of re-fetching.
                if (await HashOf(original, ct) != libraryFile.Hash)
                {
                    logger.LogInformation("Archivo local '{Name}' sin descarga: el original '{Path}' cambió de contenido", name, local.OriginalPath);
                    continue;
                }

                var placed = await DownloadsFolder.PlaceAsync(original, downloads, original.FileName, ct);
                using var tx = connection.BeginTransaction();
                tx.Add(local.Id, LibraryFile.DownloadPath, placed.RelativeTo(downloads));
                await tx.Commit();
                copied++;
                logger.LogInformation("Archivo local '{Name}' copiado a Descargas como '{File}'", name, placed.FileName);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // One unreadable or unwritable file must not block the rest, nor every later start.
                logger.LogWarning(e, "No se pudo copiar el archivo local '{Name}' ('{Path}') a Descargas", name, local.OriginalPath);
            }
        }
        return copied;
    }

    private static async Task<Hash> HashOf(AbsolutePath path, CancellationToken ct)
    {
        await using var stream = path.Read();
        return await stream.xxHash3Async(token: ct);
    }
}
