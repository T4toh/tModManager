using NexusMods.Paths;

namespace NexusMods.Sdk.IO;

/// <summary>
/// Containment checks for paths built from untrusted data (archive entry names, mod and collection manifests,
/// server-supplied file names). NexusMods.Paths keeps <c>..</c> segments as written, <c>Combine</c> doesn't
/// normalize and <c>InFolder</c> is lexical, so none of them can tell whether a path escapes its root.
/// </summary>
public static class SafePath
{
    /// <summary>
    /// True when <paramref name="path"/>, after resolving <c>.</c> and <c>..</c>, is strictly inside
    /// <paramref name="root"/> (never the root itself).
    /// </summary>
    public static bool IsStrictlyInside(string root, string path)
    {
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(rootWithSeparator, StringComparison.Ordinal);
    }

    /// <inheritdoc cref="IsStrictlyInside(string, string)"/>
    public static bool IsStrictlyInside(AbsolutePath root, AbsolutePath path) => IsStrictlyInside(root.ToString(), path.ToString());

    /// <summary>
    /// True when <paramref name="path"/> has a <c>..</c> segment.
    /// </summary>
    public static bool HasParentSegment(RelativePath path) => path.ToString().Split('/').Contains("..");

    /// <summary>
    /// True when any directory between <paramref name="root"/> (exclusive) and <paramref name="path"/> (exclusive)
    /// is a symbolic link, i.e. the path really lives somewhere else.
    /// </summary>
    public static bool IsUnderSymlink(string root, string path)
    {
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        for (var dir = Path.GetDirectoryName(path); dir is not null && dir.Length > trimmedRoot.Length; dir = Path.GetDirectoryName(dir))
        {
            if (new DirectoryInfo(dir).LinkTarget is not null) return true;
        }
        return false;
    }
}
