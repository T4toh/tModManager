using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;

namespace NexusMods.Library;

/// <summary>
/// One-time repair for local files registered before hand-added files were copied into Downloads:
/// copies each one whose original still exists and records its DownloadPath. Idempotent; a run
/// that stops halfway finishes on the next start.
/// </summary>
public sealed class LocalFileBackfill(IConnection connection, ISettingsManager settings, IFileSystem fileSystem, ILogger<LocalFileBackfill> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try { await RunAsync(cancellationToken); }
            catch (Exception e) { logger.LogWarning(e, "No se pudieron copiar los archivos locales viejos a Descargas"); }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

            var original = fileSystem.FromUnsanitizedFullPath(local.OriginalPath);
            // File.Exists is false for directories and for links to directories.
            if (!original.FileExists)
            {
                logger.LogInformation("Archivo local '{Name}' sin descarga: el original '{Path}' ya no está", libraryFile.AsLibraryItem().Name, local.OriginalPath);
                continue;
            }

            var placed = await DownloadsFolder.PlaceAsync(original, downloads, original.FileName, ct);
            using var tx = connection.BeginTransaction();
            tx.Add(local.Id, LibraryFile.DownloadPath, placed.RelativeTo(downloads));
            await tx.Commit();
            copied++;
            logger.LogInformation("Archivo local '{Name}' copiado a Descargas como '{File}'", libraryFile.AsLibraryItem().Name, placed.FileName);
        }
        return copied;
    }
}
