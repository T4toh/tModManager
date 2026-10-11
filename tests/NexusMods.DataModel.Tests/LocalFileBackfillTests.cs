using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Library;
using NexusMods.Paths;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;
using Xunit;

namespace NexusMods.DataModel.Tests;

public class LocalFileBackfillTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<LocalFileBackfillTests>(helper)
{
    private AbsolutePath Downloads => ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);

    private static async Task<NexusMods.Hashing.xxHash3.Hash> HashOf(AbsolutePath path)
    {
        await using var stream = path.Read();
        return await stream.xxHash3Async();
    }

    private async Task<LocalFile.ReadOnly> OldLocalFile(AbsolutePath original, NexusMods.Hashing.xxHash3.Hash? hash = null)
    {
        using var tx = Connection.BeginTransaction();
        var lib = new LibraryFile.New(tx, out var id)
        {
            FileName = original.FileName,
            Hash = hash ?? (original.FileExists ? await HashOf(original) : NexusMods.Hashing.xxHash3.Hash.From(42)),
            Size = Size.From(1),
            LibraryItem = new LibraryItem.New(tx, id) { Name = original.FileName },
        };
        var local = new LocalFile.New(tx, id) { LibraryFile = lib, OriginalPath = original.ToString() };
        var result = await tx.Commit();
        return result.Remap(local);
    }

    [Fact]
    public async Task OriginalPresent_CopiesAndSetsDownloadPath()
    {
        var original = TemporaryFileManager.CreateFolder().Path.Combine("Old.zip");
        File.WriteAllText(original.ToString(), "old");
        var local = await OldLocalFile(original);

        var copied = await ActivatorUtilities.CreateInstance<LocalFileBackfill>(ServiceProvider).RunAsync(default);

        copied.Should().Be(1);
        Downloads.Combine("Old.zip").FileExists.Should().BeTrue();
        original.FileExists.Should().BeTrue();
        LibraryFile.DownloadPath.Get(LocalFile.Load(Connection.Db, local.Id).AsLibraryFile()).ToString().Should().Be("Old.zip");
    }

    [Fact]
    public async Task OriginalMissing_LeavesItemUntouched()
    {
        var local = await OldLocalFile(TemporaryFileManager.CreateFolder().Path.Combine("Gone.zip"));

        var copied = await ActivatorUtilities.CreateInstance<LocalFileBackfill>(ServiceProvider).RunAsync(default);

        copied.Should().Be(0);
        LibraryFile.DownloadPath.TryGetValue(LocalFile.Load(Connection.Db, local.Id).AsLibraryFile(), out _).Should().BeFalse();
        Downloads.DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public async Task OriginalIsASymlinkToADirectory_IsSkipped()
    {
        var dir = TemporaryFileManager.CreateFolder().Path;
        var link = TemporaryFileManager.CreateFolder().Path.Combine("Dir.zip");
        Directory.CreateSymbolicLink(link.ToString(), dir.ToString());
        await OldLocalFile(link);

        var copied = await ActivatorUtilities.CreateInstance<LocalFileBackfill>(ServiceProvider).RunAsync(default);

        copied.Should().Be(0);
        Downloads.DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public async Task AlreadyHasDownloadPath_IsSkipped()
    {
        Downloads.CreateDirectory();
        var inside = Downloads.Combine("Have.zip");
        File.WriteAllText(inside.ToString(), "x");
        await LibraryService.AddLocalFile(inside);

        var copied = await ActivatorUtilities.CreateInstance<LocalFileBackfill>(ServiceProvider).RunAsync(default);

        copied.Should().Be(0);
        Downloads.EnumerateFiles("*", recursive: false).Should().ContainSingle();
    }

    [Fact]
    public async Task OriginalChangedSinceItWasAdded_IsSkipped()
    {
        var original = TemporaryFileManager.CreateFolder().Path.Combine("Changed.zip");
        File.WriteAllText(original.ToString(), "v2 overwrote the file the library knows");
        var local = await OldLocalFile(original, NexusMods.Hashing.xxHash3.Hash.From(42));

        var copied = await ActivatorUtilities.CreateInstance<LocalFileBackfill>(ServiceProvider).RunAsync(default);

        copied.Should().Be(0);
        LibraryFile.DownloadPath.TryGetValue(LocalFile.Load(Connection.Db, local.Id).AsLibraryFile(), out _).Should().BeFalse();
        Downloads.DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public async Task OneUnreadableOriginal_DoesNotStopTheOthers()
    {
        // CA1416: SetUnixFileMode is Unix-only (the whole suite is, but the analyzer wants the guard)
        if (!OperatingSystem.IsLinux()) return;
        var unreadable = TemporaryFileManager.CreateFolder().Path.Combine("Locked.zip");
        File.WriteAllText(unreadable.ToString(), "locked");
        var hashBeforeLock = await HashOf(unreadable);
        File.SetUnixFileMode(unreadable.ToString(), UnixFileMode.None);
        await OldLocalFile(unreadable, hashBeforeLock);
        var fine = TemporaryFileManager.CreateFolder().Path.Combine("Zzz-fine.zip");
        File.WriteAllText(fine.ToString(), "fine");
        var fineLocal = await OldLocalFile(fine);

        try
        {
            var copied = await ActivatorUtilities.CreateInstance<LocalFileBackfill>(ServiceProvider).RunAsync(default);

            copied.Should().Be(1);
            LibraryFile.DownloadPath.Get(LocalFile.Load(Connection.Db, fineLocal.Id).AsLibraryFile()).ToString().Should().Be("Zzz-fine.zip");
        }
        finally
        {
            File.SetUnixFileMode(unreadable.ToString(), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
