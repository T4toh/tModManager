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
}
