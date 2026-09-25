using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace NexusMods.Sdk.Library;

/// <summary>
/// Places original downloads in the downloads folder under the name Nexus gives them, deduplicating
/// by content: same name + same content reuses the existing file, same name + different content gets
/// a numeric suffix.
/// </summary>
public static class DownloadsFolder
{
    /// <summary>
    /// Places <paramref name="source"/> in <paramref name="folder"/> as <paramref name="fileName"/> and
    /// returns the final path. Never leaves a partially written file under a final name, and never lets
    /// two concurrent placements overwrite each other.
    /// </summary>
    public static async Task<AbsolutePath> PlaceAsync(AbsolutePath source, AbsolutePath folder, string fileName, CancellationToken ct)
    {
        folder.CreateDirectory();
        var candidate = folder.Combine(SanitizeFileName(fileName));
        var stem = candidate.GetFileNameWithoutExtension();
        var ext = candidate.Extension.ToString();
        Hash? sourceHash = null;

        for (var i = 1;;)
        {
            if (candidate.FileExists)
            {
                sourceHash ??= await HashOf(source, ct);
                if (candidate.FileInfo.Size == source.FileInfo.Size && await HashOf(candidate, ct) == sourceHash)
                    return candidate;

                candidate = folder.Combine($"{stem}_{i}{ext}");
                i++;
                continue;
            }

            if (await TryClaimAsync(source, candidate, ct))
                return candidate;

            // Another writer claimed `candidate` between our existence check and the move: loop
            // back without advancing, so the hash-check branch above re-evaluates that same name.
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> to a temp file next to <paramref name="candidate"/> and
    /// atomically moves it into place. Returns false (without throwing, and with the temp file
    /// cleaned up) if another writer claimed <paramref name="candidate"/> first.
    /// </summary>
    internal static async Task<bool> TryClaimAsync(AbsolutePath source, AbsolutePath candidate, CancellationToken ct)
    {
        var tmp = candidate.Parent.Combine($"{candidate.FileName}.tmp-{Guid.NewGuid():N}");
        try
        {
            await using (var src = source.Read())
            await using (var dst = tmp.Create())
                await src.CopyToAsync(dst, ct);

            try
            {
                File.Move(tmp.ToString(), candidate.ToString(), overwrite: false);
                return true;
            }
            catch (IOException) when (candidate.FileExists)
            {
                // Only a race (another writer won first) looks like this: the move failed AND the
                // candidate now exists. Anything else (disk full, read-only filesystem, ...) leaves
                // `candidate` missing, so it must propagate instead of spinning PlaceAsync's loop forever.
                return false;
            }
        }
        finally
        {
            if (tmp.FileExists) tmp.Delete();
        }
    }

    private static async Task<Hash> HashOf(AbsolutePath path, CancellationToken ct)
    {
        await using var stream = path.Read();
        return await stream.xxHash3Async(token: ct);
    }

    /// <summary>
    /// Keeps only the last path segment of an untrusted file name (from an HTTP header or Nexus
    /// metadata), so it can never place the file outside the destination folder via directory
    /// traversal. Falls back to a random name if nothing usable is left.
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        // Path.GetFileName only splits on '/' on Linux, but NexusMods.Paths turns a backslash into '/' when the
        // name is combined, so a backslash-separated "../../x" would place the file outside the folder.
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        return string.IsNullOrEmpty(name) || name is "." or ".." ? $"download-{Guid.NewGuid():N}" : name;
    }
}
