using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;
using Xunit;

namespace NexusMods.DataModel.Tests;

public class DownloadReExtractorTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<DownloadReExtractorTests>(helper)
{
    [Fact]
    public async Task RestoresEntriesFromTheOriginalDownload()
    {
        var library = ServiceProvider.GetRequiredService<ILibraryService>();
        var store = (LooseFileStore)ServiceProvider.GetRequiredService<IFileStore>();
        var reExtractor = ServiceProvider.GetRequiredService<IDownloadReExtractor>();
        var downloads = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);
        downloads.CreateDirectory();
        var src = FileSystem.GetKnownPath(KnownPath.CurrentDirectory).Combine("Resources").Combine("Lookup Anything 1.48.1-541-1-48-1-1739333325.zip");
        var inDownloads = downloads.Combine("mod.zip");
        File.Copy(src.ToString(), inDownloads.ToString(), overwrite: true);

        var local = await library.AddLocalFile(inDownloads);
        var entryHashes = LibraryArchiveFileEntry.FindByParent(Connection.Db, local.AsLibraryFile().Id)
            .Select(e => e.AsLibraryFile().Hash).ToArray();
        entryHashes.Should().NotBeEmpty();
        foreach (var h in entryHashes) store.PathFor(h).Delete();

        var restored = await reExtractor.RestoreAsync(entryHashes, default);

        restored.Should().BeEquivalentTo(entryHashes);
        foreach (var h in entryHashes) (await store.HaveFile(h)).Should().BeTrue();
    }

    [Fact]
    public async Task WithoutDownload_RestoresNothing()
    {
        var reExtractor = ServiceProvider.GetRequiredService<IDownloadReExtractor>();
        var restored = await reExtractor.RestoreAsync([NexusMods.Hashing.xxHash3.Hash.From(123)], default);
        restored.Should().BeEmpty();
    }

    [Fact]
    public async Task TopLevelLooseFile_RestoresDirectlyFromDownloadsFolder()
    {
        var library = ServiceProvider.GetRequiredService<ILibraryService>();
        var store = (LooseFileStore)ServiceProvider.GetRequiredService<IFileStore>();
        var reExtractor = ServiceProvider.GetRequiredService<IDownloadReExtractor>();
        var downloads = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);
        downloads.CreateDirectory();
        var inDownloads = downloads.Combine("loose.txt");
        await File.WriteAllTextAsync(inDownloads.ToString(), "hello loose file");

        var local = await library.AddLocalFile(inDownloads);
        var hash = local.AsLibraryFile().Hash;
        store.PathFor(hash).Delete();
        (await store.HaveFile(hash)).Should().BeFalse();

        var restored = await reExtractor.RestoreAsync([hash], default);

        restored.Should().BeEquivalentTo([hash]);
        (await store.HaveFile(hash)).Should().BeTrue();
    }
}
