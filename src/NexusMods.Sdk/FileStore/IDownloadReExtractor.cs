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
