using System.IO.Enumeration;
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

    /// <summary>
    /// True when <paramref name="path"/> itself is a symbolic link (to a file or a directory, dangling or not).
    /// </summary>
    public static bool IsSymlink(AbsolutePath path) =>
        path.FileSystem is not InMemoryFileSystem && new FileInfo(path.ToString()).LinkTarget is not null;

    /// <summary>
    /// Recursively lists the files under <paramref name="directory"/> without entering symlinked directories.
    /// File symlinks are listed (as the link) so callers see them and unlink them instead of writing through them.
    /// A name with backslashes is read back by NexusMods.Paths as separators and can alias a path outside
    /// <paramref name="directory"/>: callers check <see cref="IsStrictlyInside(AbsolutePath, AbsolutePath)"/>.
    /// </summary>
    public static IEnumerable<AbsolutePath> EnumerateFilesNoFollow(AbsolutePath directory)
    {
        // The in-memory filesystem used by tests has no symlinks and no real path behind it
        if (directory.FileSystem is InMemoryFileSystem) return directory.EnumerateFiles();

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var entries = new FileSystemEnumerable<string>(directory.ToString(), (ref FileSystemEntry entry) => entry.ToFullPath(), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0,
        };
        return entries.Select(directory.FileSystem.FromUnsanitizedFullPath);
    }
}
