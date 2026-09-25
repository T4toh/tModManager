using Microsoft.Extensions.Logging;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Settings;
using NexusMods.Sdk.Threading;

namespace NexusMods.DataModel;

/// <summary>
/// Content-addressed file store: one plain file per xxHash3, under a two-character prefix folder
/// (<c>ab/abcd…</c>), the same layout as <c>.git/objects</c>.
/// </summary>
public sealed class LooseFileStore : IFileStore
{
    private static readonly Hash EmptyFile = Array.Empty<byte>().xxHash3();
    private const string TmpMarker = ".tmp-";

    private readonly ILogger<LooseFileStore> _logger;
    private readonly AbsolutePath _root;

    /// <summary>
    /// Backups take the read lock and garbage collection the write lock, so GC never deletes a
    /// file while it is being written. Same guarantee the Nx store gave.
    /// </summary>
    internal AsyncFriendlyReaderWriterLock Lock { get; } = new();

    public LooseFileStore(ILogger<LooseFileStore> logger, ISettingsManager settingsManager, IFileSystem fileSystem)
        : this(logger, settingsManager.Get<DataModelSettings>().ArchiveLocations[0].ToPath(fileSystem)) { }

    internal LooseFileStore(ILogger<LooseFileStore> logger, AbsolutePath root)
    {
        _logger = logger;
        _root = root;
        _root.CreateDirectory();
    }

    public AbsolutePath PathFor(Hash hash)
    {
        var hex = hash.ToHex();
        return _root.Combine(hex[..2]).Combine(hex);
    }

    /// <inheritdoc />
    public ValueTask<bool> HaveFile(Hash hash) => ValueTask.FromResult(PathFor(hash).FileExists);

    /// <inheritdoc />
    /// <remarks><paramref name="deduplicate"/> is ignored: a content-addressed store always deduplicates.</remarks>
    public async Task BackupFiles(IEnumerable<ArchivedFileEntry> backups, bool deduplicate = true, CancellationToken token = default)
    {
        using var _ = Lock.ReadLock();
        var entries = backups.DistinctBy(x => x.Hash).ToArray();
        var written = 0;
        await Parallel.ForEachAsync(entries, token, async (entry, ct) =>
        {
            var dest = PathFor(entry.Hash);
            if (dest.FileExists)
            {
                try
                {
                    // Dedupe-reused file: refresh its mtime so the GC sweep's grace period protects
                    // it until whatever just referenced it (again) commits to the DB.
                    File.SetLastWriteTimeUtc(dest.ToString(), DateTime.UtcNow);
                    return;
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Vanished between the check and the touch; fall through and write it normally.
                }
            }

            dest.Parent.CreateDirectory();
            var tmp = dest.Parent.Combine($"{dest.FileName}{TmpMarker}{Guid.NewGuid():N}");
            try
            {
                await using (var src = await entry.StreamFactory.GetStreamAsync())
                await using (var dst = tmp.Create())
                    await src.CopyToAsync(dst, ct);

                Hash actual;
                await using (var check = tmp.Read())
                    actual = await check.xxHash3Async();
                if (actual != entry.Hash)
                    throw new InvalidDataException($"Hash mismatch backing up {entry.Hash.ToHex()}: content hashes to {actual.ToHex()}");

                // Another writer may have finished the same hash first; identical content, so overwriting is harmless.
                File.Move(tmp.ToString(), dest.ToString(), overwrite: true);
                Interlocked.Increment(ref written);
            }
            finally
            {
                if (tmp.FileExists) tmp.Delete();
            }
        });
        if (written > 0) _logger.LogDebug("Guardados {Count} archivos en el store", written);
    }

    /// <inheritdoc />
    public async Task ExtractFiles(IEnumerable<(Hash Hash, AbsolutePath Dest)> files, CancellationToken token = default, Action<(int Current, int Max)>? progress = null)
    {
        using var _ = Lock.ReadLock();
        var list = files.ToArray();
        var missing = list.Where(f => f.Hash != EmptyFile && !PathFor(f.Hash).FileExists).ToArray();
        if (missing.Length > 0) throw new MissingArchiveException(missing);

        var done = 0;
        await Parallel.ForEachAsync(list, token, async (f, ct) =>
        {
            f.Dest.Parent.CreateDirectory();
            if (f.Hash == EmptyFile)
            {
                f.Dest.Create().Dispose();
            }
            else
            {
                await using var src = PathFor(f.Hash).Read();
                await using var dst = f.Dest.Create();
                await src.CopyToAsync(dst, ct);
            }

            var n = Interlocked.Increment(ref done);
            if (n % 100 == 0 || n == list.Length) progress?.Invoke((n, list.Length));
        });
    }

    /// <inheritdoc />
    public async Task<Dictionary<Hash, byte[]>> ExtractFiles(IEnumerable<Hash> files, CancellationToken token = default)
    {
        var result = new Dictionary<Hash, byte[]>();
        foreach (var hash in files.Distinct())
            result[hash] = await Load(hash, token);
        return result;
    }

    /// <inheritdoc />
    public Task<Stream> GetFileStream(Hash hash, CancellationToken token = default)
    {
        if (hash == EmptyFile) return Task.FromResult<Stream>(new MemoryStream([], writable: false));
        var path = PathFor(hash);
        if (!path.FileExists) throw new MissingArchiveException(hash);
        return Task.FromResult(path.Read());
    }

    /// <inheritdoc />
    public async Task<byte[]> Load(Hash hash, CancellationToken token = default)
    {
        using var _ = Lock.ReadLock();
        if (hash == EmptyFile) return [];
        var path = PathFor(hash);
        if (!path.FileExists) throw new MissingArchiveException(hash);
        return await path.ReadAllBytesAsync(token);
    }

    /// <summary>
    /// How long an unreferenced file (hash or temp) survives a sweep. Exposed for tests.
    /// </summary>
    internal TimeSpan GracePeriod { get; set; } = TimeSpan.FromHours(1);

    // Hash.ToHex() emits uppercase hex (e.g. "4024E3632B936F67"), so that's what PathFor lays out on disk.
    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'A' and <= 'F');

    private static bool IsTwoHexChars(ReadOnlySpan<char> s) => s.Length == 2 && IsHex(s[0]) && IsHex(s[1]);

    private static bool IsHashFileName(ReadOnlySpan<char> s)
    {
        if (s.Length != 16) return false;
        foreach (var c in s)
        {
            if (!IsHex(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// Enumerates only the files this store owns: files directly inside a two-hex-char
    /// child directory of <see cref="_root"/>, that are either a 16-char hex hash whose first two
    /// characters match the containing directory, or a temp file (<see cref="TmpMarker"/>). Anything
    /// else on disk under <see cref="_root"/> (foreign files, unrelated subdirectories) is ignored:
    /// never deleted, never counted.
    /// </summary>
    private IEnumerable<AbsolutePath> EnumerateOwnedFiles()
    {
        foreach (var dir in _root.EnumerateDirectories())
        {
            var dirName = dir.FileName.ToString();
            if (!IsTwoHexChars(dirName)) continue;

            foreach (var file in dir.EnumerateFiles())
            {
                var name = file.FileName.ToString();
                if (name.Contains(TmpMarker, StringComparison.Ordinal))
                {
                    yield return file;
                    continue;
                }

                if (IsHashFileName(name) && name.StartsWith(dirName, StringComparison.Ordinal))
                    yield return file;
            }
        }
    }

    /// <summary>
    /// Deletes every stored file whose hash is not in <paramref name="live"/>, plus stale temp files.
    /// Returns the number of deleted files.
    /// </summary>
    internal int DeleteAllExcept(IReadOnlySet<Hash> live)
    {
        using var _ = Lock.WriteLock();
        var deleted = 0;
        foreach (var file in EnumerateOwnedFiles())
        {
            var name = file.FileName.ToString();
            if (name.Contains(TmpMarker, StringComparison.Ordinal))
            {
                // ponytail: young files may belong to a backup whose DB commit hasn't landed yet
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file.ToString()) > GracePeriod)
                {
                    file.Delete();
                    deleted++;
                }
                continue;
            }

            if (live.Contains(Hash.FromHex(name))) continue;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file.ToString()) <= GracePeriod) continue;
            file.Delete();
            deleted++;
        }
        return deleted;
    }

    /// <summary>
    /// Deletes every file this store owns, ignoring the grace period (this is an explicit user
    /// action, e.g. "Delete Archives" in the Storage Manager), then removes the two-hex-char
    /// directories left empty by that deletion. Foreign files/directories are never touched.
    /// Returns the number of deleted files.
    /// </summary>
    internal int DeleteAll()
    {
        using var _ = Lock.WriteLock();
        var owned = EnumerateOwnedFiles().ToArray();
        foreach (var file in owned)
            file.Delete();

        foreach (var dir in _root.EnumerateDirectories())
        {
            var dirName = dir.FileName.ToString();
            if (!IsTwoHexChars(dirName)) continue;
            if (!dir.EnumerateFiles().Any() && !dir.EnumerateDirectories().Any())
                dir.DeleteDirectory(recursive: false);
        }

        return owned.Length;
    }

    /// <summary>
    /// Total size of every file this store owns. Foreign files under <see cref="_root"/> are not counted.
    /// </summary>
    internal Size TotalSize()
    {
        var total = 0UL;
        foreach (var file in EnumerateOwnedFiles())
            total += file.FileInfo.Size.Value;
        return Size.From(total);
    }
}
