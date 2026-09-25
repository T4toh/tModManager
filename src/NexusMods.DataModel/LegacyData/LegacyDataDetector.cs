using NexusMods.Paths;
using NexusMods.Sdk.IO;

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

    /// <summary>Whether a reset was requested and is still pending (the marker exists).</summary>
    public static bool IsResetPending(IFileSystem fs, AbsolutePath? markerPath = null) =>
        ResetMarker(fs, markerPath).FileExists;

    /// <summary>
    /// Whether the database should be treated as already existing (so migrations should run against
    /// it) once a reset attempt has had a chance to run. Pure function so the tricky case — a reset
    /// was requested but its own safety guard refused it — can be unit-tested without touching disk.
    /// </summary>
    /// <param name="dirExistedBeforeReset">Whether the DB directory existed before attempting a reset.</param>
    /// <param name="resetWasPendingBefore"><see cref="IsResetPending"/>, read before attempting a reset.</param>
    /// <param name="resetWasPendingAfter"><see cref="IsResetPending"/>, read again after attempting a reset.</param>
    /// <remarks>
    /// <see cref="ResetIfRequested"/> only ever clears the marker as its last step, once it actually
    /// deleted the database — if its guard refuses instead, it throws before reaching that point and
    /// the marker survives. So "pending before, no longer pending after" is the only reliable signal
    /// that a reset really ran; treating "marker present" alone as "no model" (as an earlier version
    /// of this logic did) makes a refused reset fall through to <c>InitialSetup</c> on a database
    /// that still has a schema version, which throws.
    /// </remarks>
    public static bool ModelExistsAfterReset(bool dirExistedBeforeReset, bool resetWasPendingBefore, bool resetWasPendingAfter)
    {
        var resetRan = resetWasPendingBefore && !resetWasPendingAfter;
        return dirExistedBeforeReset && !resetRan;
    }

    /// <summary>Deletes the <c>.nx</c> archives and the database if a reset was requested. Returns true if it did.</summary>
    /// <param name="archivesRoot">Where the <c>.nx</c> archives live.</param>
    /// <param name="mnemonicDbPath">The database directory to delete.</param>
    /// <param name="fs">The file system.</param>
    /// <param name="markerPath">Test seam for the marker file's location.</param>
    /// <param name="dataDirectoryRoot">
    /// Test seam for the app's data directory root (guarded against below); defaults to the real
    /// <c>XDG_DATA_HOME/tModManager</c> in production. Tests must always override this to a temp
    /// folder, so that a regression in the guard below can never delete real user data.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="mnemonicDbPath"/> is, or is an ancestor of, the app's data directory or
    /// <paramref name="archivesRoot"/>: deleting it would wipe out far more than the database.
    /// Paths are compared after resolving <c>..</c> and symlinks, so this can't be bypassed by an
    /// unnormalized configured path.
    /// </exception>
    public static bool ResetIfRequested(
        AbsolutePath archivesRoot,
        AbsolutePath mnemonicDbPath,
        IFileSystem fs,
        AbsolutePath? markerPath = null,
        AbsolutePath? dataDirectoryRoot = null)
    {
        var marker = ResetMarker(fs, markerPath);
        if (!marker.FileExists) return false;

        var dataRoot = dataDirectoryRoot ?? fs.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine(NexusMods.Sdk.ApplicationConstants.DataDirectoryName);
        if (IsSelfOrAncestorOf(mnemonicDbPath, dataRoot) || IsSelfOrAncestorOf(mnemonicDbPath, archivesRoot))
            throw new InvalidOperationException(
                $"Me niego a reiniciar: la ruta de la base de datos ({mnemonicDbPath}) es, o contiene, el directorio de datos o la carpeta de archivos; borrarla perdería mucho más que la base.");

        if (archivesRoot.DirectoryExists())
            foreach (var nx in archivesRoot.EnumerateFiles("*.nx", recursive: false)) nx.Delete();
        if (mnemonicDbPath.DirectoryExists()) mnemonicDbPath.DeleteDirectoryNoFollow();
        marker.Delete();
        return true;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is the same directory as, or an ancestor of,
    /// <paramref name="other"/>, comparing resolved real paths (so an unnormalized <c>..</c> or a
    /// symlink can't make two identical locations look different).
    /// </summary>
    private static bool IsSelfOrAncestorOf(AbsolutePath candidate, AbsolutePath other)
    {
        var candidateReal = RealPath.Resolve(candidate);
        var otherReal = RealPath.Resolve(other);
        if (string.Equals(candidateReal, otherReal, StringComparison.Ordinal)) return true;

        var prefix = candidateReal.EndsWith(Path.DirectorySeparatorChar) ? candidateReal : candidateReal + Path.DirectorySeparatorChar;
        return otherReal.StartsWith(prefix, StringComparison.Ordinal);
    }
}
