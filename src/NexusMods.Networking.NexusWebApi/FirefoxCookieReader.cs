using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace NexusMods.Networking.NexusWebApi;

/// <summary>
/// Reads cookies from the Firefox profile on Linux to support authenticated
/// requests to www.nexusmods.com that require session cookies.
/// </summary>
internal static class FirefoxCookieReader
{
    private const string LibSqlite = "libsqlite3.so.0";
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_OPEN_READWRITE = 2;

    [DllImport(LibSqlite, EntryPoint = "sqlite3_open_v2")]
    private static extern int Open(string filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(LibSqlite, EntryPoint = "sqlite3_prepare_v2")]
    private static extern int Prepare(IntPtr db, string sql, int nByte, out IntPtr stmt, IntPtr tail);

    [DllImport(LibSqlite, EntryPoint = "sqlite3_step")]
    private static extern int Step(IntPtr stmt);

    [DllImport(LibSqlite, EntryPoint = "sqlite3_column_text")]
    private static extern IntPtr ColumnText(IntPtr stmt, int col);

    [DllImport(LibSqlite, EntryPoint = "sqlite3_finalize")]
    private static extern int Finalize(IntPtr stmt);

    [DllImport(LibSqlite, EntryPoint = "sqlite3_close")]
    private static extern int Close(IntPtr db);

    /// <summary>
    /// Attempts to build a Cookie header string for nexusmods.com from the Firefox profile.
    /// Returns null if no Firefox profile is found or cookies can't be read.
    /// </summary>
    public static string? TryGetNexusModsCookieHeader(ILogger logger)
    {
        try
        {
            var cookiesPath = FindFirefoxCookiesDb();
            if (cookiesPath is null)
            {
                logger.LogDebug("Firefox cookies.sqlite not found — skipping cookie-based download auth");
                return null;
            }

            return ReadCookieHeader(cookiesPath, logger);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read Firefox cookies");
            return null;
        }
    }

    /// <summary>
    /// Reads the nexusmods.com cookies from a copy of <paramref name="cookiesPath"/>, so Firefox's lock doesn't get in the way.
    /// The <c>-wal</c> file is copied too: Firefox keeps recent writes there (a fresh login) until it checkpoints, often only
    /// on exit, and the main file alone wouldn't have them.
    /// </summary>
    internal static string? ReadCookieHeader(string cookiesPath, ILogger logger)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"nm_ff_cookies_{Guid.NewGuid():N}.sqlite");
        try
        {
            // ponytail: the two copies aren't atomic; SQLite drops WAL frames that don't checksum, so a write racing the copy only loses that write
            File.Copy(cookiesPath, tempPath, overwrite: true);
            if (File.Exists(cookiesPath + "-wal"))
                File.Copy(cookiesPath + "-wal", tempPath + "-wal", overwrite: true);
            return ReadCookies(tempPath, logger);
        }
        finally
        {
            foreach (var path in new[] { tempPath, tempPath + "-wal", tempPath + "-shm" })
            {
                try { File.Delete(path); } catch { /* ignore cleanup failures */ }
            }
        }
    }

    private static string? ReadCookies(string dbPath, ILogger logger)
    {
        // Read-write: replaying the copied WAL needs a -shm next to it. Only ever our temp copy.
        if (Open(dbPath, out var db, SQLITE_OPEN_READWRITE, IntPtr.Zero) != SQLITE_OK)
        {
            logger.LogDebug("Could not open Firefox cookies.sqlite");
            return null;
        }

        try
        {
            const string sql = """
                SELECT name, value FROM moz_cookies
                WHERE host LIKE '%nexusmods.com%'
                ORDER BY name
                """;

            if (Prepare(db, sql, -1, out var stmt, IntPtr.Zero) != SQLITE_OK)
                return null;

            var parts = new List<string>();
            while (Step(stmt) == SQLITE_ROW)
            {
                var name = Marshal.PtrToStringUTF8(ColumnText(stmt, 0));
                var value = Marshal.PtrToStringUTF8(ColumnText(stmt, 1));
                if (name is not null && value is not null)
                    parts.Add($"{name}={value}");
            }

            Finalize(stmt);

            if (parts.Count == 0)
            {
                logger.LogDebug("No nexusmods.com cookies found in Firefox profile");
                return null;
            }

            logger.LogDebug("Found {Count} Firefox cookies for nexusmods.com", parts.Count);
            return string.Join("; ", parts);
        }
        finally
        {
            Close(db);
        }
    }

    private static string? FindFirefoxCookiesDb()
    {
        // Search common Firefox profile locations on Linux
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var searchRoots = new[]
        {
            Path.Combine(home, ".config", "mozilla", "firefox"),
            Path.Combine(home, ".mozilla", "firefox"),
        };

        foreach (var root in searchRoots)
        {
            if (!Directory.Exists(root)) continue;

            var iniPath = Path.Combine(root, "profiles.ini");
            if (!File.Exists(iniPath)) continue;

            var profilePath = ParseDefaultProfilePath(iniPath, root);
            if (profilePath is null) continue;

            var cookiesDb = Path.Combine(profilePath, "cookies.sqlite");
            if (File.Exists(cookiesDb))
                return cookiesDb;
        }

        return null;
    }

    private static string? ParseDefaultProfilePath(string iniPath, string root)
    {
        // Look for the profile referenced by an [Install...] Default= entry first,
        // then fall back to any Profile with IsRelative/Path.
        string? installDefault = null;
        string? firstProfilePath = null;

        string? currentSection = null;
        string? sectionPath = null;
        bool sectionIsRelative = true;

        foreach (var line in File.ReadLines(iniPath))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
            {
                // Flush previous section
                if (currentSection is not null && sectionPath is not null)
                {
                    var resolved = sectionIsRelative
                        ? Path.Combine(root, sectionPath)
                        : sectionPath;
                    firstProfilePath ??= resolved;
                }

                currentSection = trimmed;
                sectionPath = null;
                sectionIsRelative = true;
                continue;
            }

            var eq = trimmed.IndexOf('=');
            if (eq < 0) continue;
            var key = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..].Trim();

            if (currentSection is not null && currentSection.StartsWith("[Install", StringComparison.OrdinalIgnoreCase) && key.Equals("Default", StringComparison.OrdinalIgnoreCase))
                installDefault = value;

            if (key.Equals("Path", StringComparison.OrdinalIgnoreCase))
                sectionPath = value;
            if (key.Equals("IsRelative", StringComparison.OrdinalIgnoreCase))
                sectionIsRelative = value == "1";
        }

        // Flush last section
        if (currentSection is not null && sectionPath is not null)
        {
            var resolved = sectionIsRelative ? Path.Combine(root, sectionPath) : sectionPath;
            firstProfilePath ??= resolved;
        }

        if (installDefault is not null)
            return Path.Combine(root, installDefault);

        return firstProfilePath;
    }
}
