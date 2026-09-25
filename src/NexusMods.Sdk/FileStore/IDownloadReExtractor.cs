using NexusMods.Hashing.xxHash3;

namespace NexusMods.Sdk.FileStore;

/// <summary>
/// Puts missing files back into the <see cref="IFileStore"/> by extracting them again from the
/// original downloads they came from.
/// </summary>
public interface IDownloadReExtractor
{
    /// <summary>Returns the hashes that are in the store after the call.</summary>
    Task<IReadOnlySet<Hash>> RestoreAsync(IReadOnlyCollection<Hash> missing, CancellationToken ct);
}

/// <summary>
/// Extensions for <see cref="IDownloadReExtractor"/>.
/// </summary>
public static class DownloadReExtractorExtensions
{
    /// <summary>
    /// Restores, from the original downloads, whichever of <paramref name="hashes"/> the store doesn't have.
    /// Returns how many were missing and how many of those were restored (equal means nothing is missing now).
    /// </summary>
    public static async Task<(int Missing, int Restored)> RestoreMissingAsync(
        this IDownloadReExtractor reExtractor,
        IFileStore store,
        IEnumerable<Hash> hashes,
        CancellationToken ct)
    {
        var missing = new List<Hash>();
        foreach (var hash in hashes.Distinct())
        {
            if (!await store.HaveFile(hash)) missing.Add(hash);
        }

        if (missing.Count == 0) return (0, 0);
        var restored = await reExtractor.RestoreAsync(missing, ct);
        return (missing.Count, restored.Count);
    }
}
