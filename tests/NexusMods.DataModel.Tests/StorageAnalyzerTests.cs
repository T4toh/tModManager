using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.DataModel.Storage;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;
using Xunit;

namespace NexusMods.DataModel.Tests;

public class StorageAnalyzerTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<StorageAnalyzerTests>(helper)
{
    [Fact]
    public async Task DeleteDownloadsAsync_DeletesOnlyDownloadsTheLibraryRecorded()
    {
        // The downloads folder is configurable: pointed at ~/Downloads it holds the user's own files too
        var storageAnalyzer = ServiceProvider.GetRequiredService<IStorageAnalyzer>();
        var libraryService = ServiceProvider.GetRequiredService<ILibraryService>();
        var downloads = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);
        downloads.CreateDirectory();

        var recorded = downloads.Combine("mod.txt");
        await File.WriteAllTextAsync(recorded.ToString(), "mod contents");
        await libraryService.AddLocalFile(recorded);

        var usersOwn = downloads.Combine("tax-return.pdf");
        await File.WriteAllTextAsync(usersOwn.ToString(), "not a mod");
        var nestedFile = downloads.Combine("subdir/nested.txt");
        nestedFile.Parent.CreateDirectory();
        await File.WriteAllTextAsync(nestedFile.ToString(), "should survive");

        await storageAnalyzer.DeleteDownloadsAsync();

        recorded.FileExists.Should().BeFalse();
        usersOwn.FileExists.Should().BeTrue();
        nestedFile.FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteDownloadsAsync_SkipsInProgressPartials()
    {
        var storageAnalyzer = ServiceProvider.GetRequiredService<IStorageAnalyzer>();
        var downloads = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);
        downloads.CreateDirectory();
        var partial = downloads.Combine($"mod.zip.tmp-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(partial.ToString(), "half written");

        await storageAnalyzer.DeleteDownloadsAsync();

        partial.FileExists.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeletePhysicalFilesAsync_KeepNewest_KeepsOnlyTheLatestSnapshot(bool keepNewest)
    {
        var analyzer = (StorageAnalyzer)ServiceProvider.GetRequiredService<IStorageAnalyzer>();
        var backups = TemporaryFileManager.CreateFolder().Path;
        analyzer.BackupsFolderProvider = () => backups;
        string[] names = ["20260101_000000", "20260103_000000", "20260102_000000"];
        foreach (var name in names)
        {
            backups.Combine(name).CreateDirectory();
            await File.WriteAllTextAsync(backups.Combine(name).Combine("mod.archive").ToString(), name);
        }

        await analyzer.DeletePhysicalFilesAsync(keepNewest);

        backups.EnumerateDirectories(recursive: false).Select(d => d.FileName.ToString())
            .Should().BeEquivalentTo(keepNewest ? ["20260103_000000"] : Array.Empty<string>());
        if (keepNewest) backups.Combine("20260103_000000").Combine("mod.archive").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task DeletePhysicalFilesAsync_DoesNotFollowSymlinksOutOfTheBackup()
    {
        // Deep Clean moves mod folders as-is, so a backup can hold a symlink to a folder elsewhere.
        var analyzer = (StorageAnalyzer)ServiceProvider.GetRequiredService<IStorageAnalyzer>();
        var backups = TemporaryFileManager.CreateFolder().Path;
        var outside = TemporaryFileManager.CreateFolder().Path;
        var canary = outside.Combine("sub/canary.txt");
        canary.Parent.CreateDirectory();
        await File.WriteAllTextAsync(canary.ToString(), "must survive");
        analyzer.BackupsFolderProvider = () => backups;
        backups.Combine("20260101_000000").CreateDirectory();
        File.CreateSymbolicLink(backups.Combine("20260101_000000/linked-mod").ToString(), outside.ToString());

        var stats = await analyzer.GetStorageStatsAsync();
        await analyzer.DeletePhysicalFilesAsync();

        backups.Combine("20260101_000000").DirectoryExists().Should().BeFalse();
        canary.FileExists.Should().BeTrue();
        stats.CyberpunkBackupsSize.Value.Should().Be(0, "the size walk must not count files behind a symlink");
    }

    [Fact]
    public async Task RunDeepCleanOnAllLoadouts_WithSeveralLoadouts_RefusesBeforeTouchingAnything()
    {
        // Never run this without the guard: Deep Clean writes its backups to the real XDG data folder
        var analyzer = ServiceProvider.GetRequiredService<IStorageAnalyzer>();
        await CreateLoadout();
        await CreateLoadout();

        var act = () => analyzer.RunDeepCleanOnAllLoadoutsAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*loadouts*");
    }
}
