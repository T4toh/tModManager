using NexusMods.Paths;

namespace NexusMods.DataModel.LegacyData;

/// <summary>
/// Resolves a path to its canonical form the way POSIX <c>realpath(3)</c> would: collapses
/// <c>..</c>/<c>.</c> and follows symlinks at every level (not just the final path component).
/// Used wherever comparing two configured paths for "are these actually the same place on disk"
/// matters for data safety — string/segment comparisons on the raw path can be fooled by an
/// unnormalized <c>..</c> or by either side being a symlink to the other.
/// </summary>
internal static class RealPath
{
    public static string Resolve(AbsolutePath path) => Resolve(path.ToString());

    public static string Resolve(string path, int depth = 0)
    {
        // Guards against symlink cycles (a -> b -> a): fall back to the normalized-but-unresolved
        // path rather than recursing forever or overflowing the stack.
        if (depth > 40) return Path.GetFullPath(path);

        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent) || parent == full) return full;

        // Resolve the parent chain first, so a symlinked ancestor (e.g. Downloads living inside a
        // symlinked app-data folder) is accounted for before we even look at the last component.
        var parentResolved = Resolve(parent, depth + 1);
        var candidate = Path.Combine(parentResolved, Path.GetFileName(full));

        FileSystemInfo? info = Directory.Exists(candidate) ? new DirectoryInfo(candidate)
            : File.Exists(candidate) ? new FileInfo(candidate)
            : null;

        var target = info?.ResolveLinkTarget(returnFinalTarget: false);
        return target is null ? candidate : Resolve(target.FullName, depth + 1);
    }
}
