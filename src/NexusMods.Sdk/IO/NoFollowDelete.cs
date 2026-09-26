using NexusMods.Paths;

namespace NexusMods.Sdk.IO;

/// <summary>
/// Recursive delete that never follows symbolic links.
/// </summary>
public static class NoFollowDelete
{
    /// <summary>
    /// Deletes <paramref name="path"/> and everything under it. Symlinks (including <paramref name="path"/> itself)
    /// are removed as links; their targets are never touched.
    /// </summary>
    /// <remarks>
    /// Use this instead of <c>AbsolutePath.DeleteDirectory(recursive: true)</c>: NexusMods.Paths descends into
    /// symlinked directories and deletes their contents. Every Wine prefix ships <c>dosdevices/z: -&gt; /</c>,
    /// so deleting one that way deletes files all over the disk.
    /// </remarks>
    public static void DeleteDirectoryNoFollow(this AbsolutePath path)
    {
        // The in-memory filesystem used by tests has no symlinks and no real path behind it.
        if (path.FileSystem is InMemoryFileSystem)
        {
            path.DeleteDirectory(recursive: true);
            return;
        }

        Directory.Delete(path.ToString(), recursive: true);
    }

    /// <summary>
    /// Enumeration options that recurse into subdirectories without entering symlinked ones.
    /// </summary>
    public static EnumerationOptions RecurseWithoutSymlinks => new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };
}
