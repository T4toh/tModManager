using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace NexusMods.Networking.NexusWebApi.Tests;

public class FirefoxCookieReaderTests
{
    [DllImport("libsqlite3.so.0", EntryPoint = "sqlite3_open")]
    private static extern int Open(string filename, out IntPtr db);

    [DllImport("libsqlite3.so.0", EntryPoint = "sqlite3_exec")]
    private static extern int Exec(IntPtr db, string sql, IntPtr callback, IntPtr arg, IntPtr errmsg);

    [DllImport("libsqlite3.so.0", EntryPoint = "sqlite3_close")]
    private static extern int Close(IntPtr db);

    [Fact]
    public void ReadsCookiesStillInTheWal()
    {
        // Firefox running: a fresh login sits in cookies.sqlite-wal until Firefox checkpoints (often only on exit)
        var dir = Directory.CreateTempSubdirectory("ff_cookies_test");
        var path = Path.Combine(dir.FullName, "cookies.sqlite");
        Open(path, out var db).Should().Be(0);
        try
        {
            Exec(db, """
                PRAGMA journal_mode=WAL;
                PRAGMA wal_autocheckpoint=0;
                CREATE TABLE moz_cookies (name TEXT, value TEXT, host TEXT);
                INSERT INTO moz_cookies VALUES ('cf_clearance', 'a', '.nexusmods.com');
                """, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero).Should().Be(0);
            File.Exists(path + "-wal").Should().BeTrue();

            // The connection stays open, so nothing reaches the main file
            FirefoxCookieReader.ReadCookieHeader(path, NullLogger.Instance)
                .Should().Be("cf_clearance=a");
        }
        finally
        {
            Close(db);
            dir.Delete(recursive: true);
        }
    }
}
