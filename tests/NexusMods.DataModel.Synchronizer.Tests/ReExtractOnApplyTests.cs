using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

public class ReExtractOnApplyTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<ReExtractOnApplyTests>(helper)
{
    [Fact]
    public async Task Apply_AfterStoreWiped_ReExtractsFromDownloads()
    {
        var downloads = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);
        downloads.CreateDirectory();
        var zip = downloads.Combine("mod.zip");
        await using (var fs = zip.Create())
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            await using var w = new StreamWriter(archive.CreateEntry("archive/pc/mod/test.archive").Open());
            await w.WriteAsync("contenido de prueba");
        }

        var loadout = await CreateLoadout();
        var local = await LibraryService.AddLocalFile(zip);
        await LoadoutManager.InstallItem(local.AsLibraryFile().AsLibraryItem(), loadout);
        loadout = loadout.Rebase();

        var hashes = LibraryArchiveFileEntry.FindByParent(Connection.Db, local.AsLibraryFile().Id).Select(e => e.AsLibraryFile().Hash);
        foreach (var h in hashes) ((LooseFileStore)FileStore).PathFor(h).Delete();

        await Synchronizer.Synchronize(loadout);

        GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "archive/pc/mod/test.archive")).FileExists.Should().BeTrue();
    }
}
