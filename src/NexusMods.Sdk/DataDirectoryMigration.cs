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
}
