using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task DeleteDownloadsAsync_DeletesTopLevelFilesOnly_SubfoldersSurvive()
    {
        var storageAnalyzer = ServiceProvider.GetRequiredService<IStorageAnalyzer>();
        var downloads = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);
        downloads.CreateDirectory();

        var topLevelFile = downloads.Combine("mod.zip");
        await File.WriteAllTextAsync(topLevelFile.ToString(), "archive contents");

        var subDir = downloads.Combine("subdir");
        subDir.CreateDirectory();
        var nestedFile = subDir.Combine("nested.txt");
        await File.WriteAllTextAsync(nestedFile.ToString(), "should survive");

        await storageAnalyzer.DeleteDownloadsAsync();

        topLevelFile.FileExists.Should().BeFalse();
        downloads.DirectoryExists().Should().BeTrue();
        subDir.DirectoryExists().Should().BeTrue();
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
}
