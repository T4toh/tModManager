using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.DataModel;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;
using Xunit;

namespace NexusMods.Collections.Tests;

/// <summary>
/// <see cref="CollectionDownloader.ValidateStatusAsync"/> must not send an item back to "not downloaded"
/// (and so to a Nexus re-download) while its original download is still in Downloads: install re-extracts from it.
/// </summary>
public class ValidateStatusTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<ValidateStatusTests>(helper)
{
    [Fact]
    public async Task StoreWiped_DownloadOnDisk_StaysInLibrary_DownloadGone_NotDownloaded()
    {
        var downloads = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);
        downloads.CreateDirectory();
        var zip = downloads.Combine("item.zip");
        await using (var fs = zip.Create())
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            await using var w = new StreamWriter(archive.CreateEntry("archive/pc/mod/item.archive").Open());
            await w.WriteAsync("contenido");
        }

        var local = await LibraryService.AddLocalFile(zip);
        foreach (var entry in LibraryArchiveFileEntry.FindByParent(Connection.Db, local.AsLibraryFile().Id))
            ((LooseFileStore)FileStore).PathFor(entry.AsLibraryFile().Hash).Delete();

        var downloader = ServiceProvider.GetRequiredService<CollectionDownloader>();
        var status = new CollectionDownloadStatus.InLibrary(local.AsLibraryFile().AsLibraryItem());

        (await downloader.ValidateStatusAsync(status)).IsInLibrary(out _).Should().BeTrue();

        zip.Delete();
        (await downloader.ValidateStatusAsync(status)).IsNotDownloaded().Should().BeTrue();
    }
}
