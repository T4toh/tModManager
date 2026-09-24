using Microsoft.Extensions.Logging;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.FileExtractor;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;

namespace NexusMods.Library;

/// <summary>
/// Puts missing files back into the <see cref="IFileStore"/> by re-extracting them from the original
/// download (kept under <see cref="DownloadsSettings.Folder"/>) they came from.
/// </summary>
internal sealed class DownloadReExtractor(
    IConnection connection,
    IFileStore store,
    IFileExtractor extractor,
    TemporaryFileManager temp,
    ISettingsManager settings,
    IFileSystem fs,
    ILogger<DownloadReExtractor> logger) : IDownloadReExtractor
{
    public async Task<IReadOnlySet<Hash>> RestoreAsync(IReadOnlyCollection<Hash> missing, CancellationToken ct)
    {
        var restored = new HashSet<Hash>();
        if (missing.Count == 0) return restored;
        var db = connection.Db;
        var downloads = settings.Get<DownloadsSettings>().Folder.ToPath(fs);

        // Group the wanted hashes by the top-level download that contains them.
        var byDownload = new Dictionary<AbsolutePath, List<(RelativePath[] Chain, Hash Hash)>>();
        foreach (var hash in missing.Distinct())
        {
            foreach (var file in LibraryFile.FindByHash(db, hash))
            {
                if (!TryChain(file, out var top, out var chain)) continue;
                if (!LibraryFile.DownloadPath.TryGetValue(top, out var rel)) continue;
                var archive = downloads.Combine(rel);
                if (!archive.FileExists) continue;
                if (!byDownload.TryGetValue(archive, out var list)) byDownload[archive] = list = [];
                list.Add((chain, hash));
                break;
            }
        }

        foreach (var (archive, wanted) in byDownload)
        {
            var before = restored.Count;
            try
            {
                await using var dir = temp.CreateFolder();
                await extractor.ExtractAllAsync(archive, dir.Path, token: ct);

                foreach (var (chain, hash) in wanted)
                {
                    // Nested chains extract intermediate archives into their own temp folders, which must
                    // stay on disk until BackupFiles has read the resolved file — dispose them only after.
                    var nestedDirs = new List<TemporaryPath>();
                    try
                    {
                        var path = await Resolve(dir.Path, chain, nestedDirs, ct);
                        if (path is null || !path.Value.FileExists) continue;
                        var entry = new ArchivedFileEntry(new NativeFileStreamFactory(path.Value), hash, path.Value.FileInfo.Size);
                        try
                        {
                            await store.BackupFiles([entry], token: ct);
                            restored.Add(hash);
                        }
                        catch (InvalidDataException ex)
                        {
                            logger.LogWarning(ex, "El contenido reextraído de {Archive} no coincide con el hash esperado {Hash}", archive.FileName, hash);
                        }
                    }
                    finally
                    {
                        foreach (var nested in nestedDirs) await nested.DisposeAsync();
                    }
                }
                logger.LogInformation("Reextraídos {Count} archivos desde {Archive}", restored.Count - before, archive.FileName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo reextraer desde {Archive}", archive);
            }
        }

        // Top-level loose files (no archive chain): restore straight from the downloaded file itself.
        foreach (var hash in missing.Distinct())
        {
            if (restored.Contains(hash)) continue;
            foreach (var file in LibraryFile.FindByHash(db, hash))
            {
                if (TryChain(file, out _, out _)) continue; // handled above (archive entry)
                if (!LibraryFile.DownloadPath.TryGetValue(file, out var rel)) continue;
                var path = downloads.Combine(rel);
                if (!path.FileExists) continue;
                try
                {
                    await store.BackupFiles([new ArchivedFileEntry(new NativeFileStreamFactory(path), hash, path.FileInfo.Size)], token: ct);
                    restored.Add(hash);
                }
                catch (InvalidDataException ex)
                {
                    logger.LogWarning(ex, "El contenido de {Path} no coincide con el hash esperado {Hash}", path.FileName, hash);
                }
                break;
            }
        }

        return restored;
    }

    /// <summary>
    /// Walks up <see cref="LibraryArchiveFileEntry"/> parents to the top-level file. <paramref name="chain"/>
    /// holds the entry paths from the top-level archive down (more than one element means nested archives).
    /// Returns false for a top-level file that is not itself an archive entry.
    /// </summary>
    private static bool TryChain(LibraryFile.ReadOnly file, out LibraryFile.ReadOnly top, out RelativePath[] chain)
    {
        var parts = new List<RelativePath>();
        var current = file;
        while (current.TryGetAsLibraryArchiveFileEntry(out var entry))
        {
            parts.Add(entry.Path);
            current = entry.Parent.AsLibraryFile();
        }
        parts.Reverse();
        top = current;
        chain = parts.ToArray();
        return parts.Count > 0;
    }

    /// <summary>
    /// Resolves the final file for a (possibly nested) chain, extracting one intermediate archive per
    /// nesting level into a temp folder added to <paramref name="dirs"/>; the caller disposes them once
    /// it's done reading the resolved file.
    /// </summary>
    private async Task<AbsolutePath?> Resolve(AbsolutePath root, RelativePath[] chain, List<TemporaryPath> dirs, CancellationToken ct)
    {
        var path = root.Combine(chain[0]);
        for (var i = 1; i < chain.Length; i++)
        {
            if (!path.FileExists) return null;
            var nested = temp.CreateFolder();
            dirs.Add(nested);
            await extractor.ExtractAllAsync(path, nested.Path, token: ct);
            path = nested.Path.Combine(chain[i]);
        }
        return path;
    }
}
