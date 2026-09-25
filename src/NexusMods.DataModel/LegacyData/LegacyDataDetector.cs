using NexusMods.Paths;

namespace NexusMods.DataModel.LegacyData;

/// <summary>
/// Finds data left by versions that stored mods in the Nexus <c>.nx</c> format.
/// </summary>
public static class LegacyDataDetector
{
    public static bool HasNxArchives(AbsolutePath archivesRoot) =>
        archivesRoot.DirectoryExists() && archivesRoot.EnumerateFiles("*.nx", recursive: false).Any();

    /// <summary>Downloads folder shared with the official NexusMods.App.</summary>
    public static AbsolutePath LegacyDownloadsFolder(IFileSystem fs) =>
        fs.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine("NexusMods.App").Combine("Downloads");

    /// <summary>Where Deep Clean used to put its backups.</summary>
    public static AbsolutePath LegacyBackupsFolder(IFileSystem fs) =>
        fs.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine("NexusMods.App").Combine("CyberpunkBackups");

    private static AbsolutePath ResetMarker(IFileSystem fs, AbsolutePath? overrideMarkerPath = null)
    {
        if (overrideMarkerPath != null) return overrideMarkerPath.Value;
        return fs.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine(NexusMods.Sdk.ApplicationConstants.DataDirectoryName).Combine("reset-on-start");
    }

    /// <summary>
    /// The database cannot be deleted while it is open, so the wizard leaves a marker and restarts;
    /// <see cref="ResetIfRequested"/> runs before the connection opens.
    /// </summary>
    public static void RequestResetOnStart(IFileSystem fs, AbsolutePath? markerPath = null)
    {
        var marker = ResetMarker(fs, markerPath);
        marker.Parent.CreateDirectory();
        marker.Create().Dispose();
    }

    /// <summary>Deletes the <c>.nx</c> archives and the database if a reset was requested. Returns true if it did.</summary>
    public static bool ResetIfRequested(AbsolutePath archivesRoot, AbsolutePath mnemonicDbPath, IFileSystem fs, AbsolutePath? markerPath = null)
    {
        var marker = ResetMarker(fs, markerPath);
        if (!marker.FileExists) return false;
        if (archivesRoot.DirectoryExists())
            foreach (var nx in archivesRoot.EnumerateFiles("*.nx", recursive: false)) nx.Delete();
        if (mnemonicDbPath.DirectoryExists()) mnemonicDbPath.DeleteDirectory(recursive: true);
        marker.Delete();
        return true;
    }
}
