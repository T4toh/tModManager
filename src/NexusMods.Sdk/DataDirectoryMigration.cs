using NexusMods.Paths;

namespace NexusMods.Sdk;

/// <summary>
/// One-time move of the data directory after the rename to tModManager.
/// </summary>
public static class DataDirectoryMigration
{
    /// <summary>
    /// Moves <see cref="ApplicationConstants.LegacyDataDirectoryName"/> to <see cref="ApplicationConstants.DataDirectoryName"/>
    /// under <paramref name="basePath"/> (e.g. XDG_DATA_HOME). Does nothing if the new directory already exists.
    /// </summary>
    /// <returns>True if a move happened.</returns>
    public static bool MigrateLegacyDataDirectory(AbsolutePath basePath)
    {
        var newPath = basePath.Combine(ApplicationConstants.DataDirectoryName);
        var legacyPath = basePath.Combine(ApplicationConstants.LegacyDataDirectoryName);
        if (newPath.DirectoryExists() || !legacyPath.DirectoryExists()) return false;

        Directory.Move(legacyPath.ToString(), newPath.ToString());
        return true;
    }

    /// <summary>
    /// One-time copy of what tModManager used to keep in the official app's <c>NexusMods.App</c> folder under
    /// <paramref name="basePath"/>: the configs and the file hash database (its upstream source is discontinued,
    /// and without it there is no vanilla file list). Copies, never moves: the official app keeps its own.
    /// A config that points at a <c>NexusMods.App/</c> path is skipped so it falls back to the new defaults
    /// (and a config written by the official app is never imported).
    /// </summary>
    public static void CopyUpstreamDataOnce(AbsolutePath basePath)
    {
        var upstream = basePath.Combine(ApplicationConstants.UpstreamDirectoryName);
        var ours = basePath.Combine(ApplicationConstants.DataDirectoryName);
        CopyOnce(upstream.Combine("Configs"), ours.Combine("Configs"),
            include: file => !File.ReadAllText(file).Contains($"{ApplicationConstants.UpstreamDirectoryName}/", StringComparison.Ordinal));
        CopyOnce(upstream.Combine("FileHashesDatabase"), ours.Combine("FileHashesDatabase"), include: _ => true);
    }

    private static void CopyOnce(AbsolutePath from, AbsolutePath to, Func<string, bool> include)
    {
        if (to.DirectoryExists() || !from.DirectoryExists()) return;

        // Copy into a temp sibling and rename, so an interrupted copy is retried instead of leaving half a folder
        var source = from.ToString();
        var tmp = $"{to}.tmp-{Guid.NewGuid():N}";
        foreach (var file in Directory.EnumerateFiles(source, "*", IO.NoFollowDelete.RecurseWithoutSymlinks))
        {
            if (!include(file)) continue;
            var destination = Path.Combine(tmp, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        Directory.CreateDirectory(tmp);
        Directory.CreateDirectory(Path.GetDirectoryName(to.ToString())!);
        Directory.Move(tmp, to.ToString());
    }
}
