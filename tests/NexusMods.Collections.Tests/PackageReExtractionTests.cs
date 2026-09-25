using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.DataModel;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;
using Xunit;

namespace NexusMods.Collections.Tests;

/// <summary>
/// Offline regression coverage for the collection-package re-extraction fix in
/// <see cref="InstallCollectionJob"/>: when the package is present in Downloads but its store copy
/// (including collection.json) was wiped (e.g. by Storage Manager's "delete archives"), the missing
/// entries must be restorable straight from the package via <see cref="IDownloadReExtractor"/>,
/// without re-downloading. Doesn't exercise the full job (which needs a real collection revision from
/// the network) — it exercises the exact restore-then-parse sequence the job now performs.
/// </summary>
public class PackageReExtractionTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<PackageReExtractionTests>(helper)
{
    [Fact]
    public async Task CollectionJson_RestoredFromDownloadedPackage_AfterStoreWipe()
    {
        var downloads = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);
        downloads.CreateDirectory();
        var zip = downloads.Combine("collection.zip");
        await using (var fs = zip.Create())
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            await using var w = new StreamWriter(archive.CreateEntry("collection.json").Open());
            await w.WriteAsync("""{"info":{"name":"Test Collection","domainName":"cyberpunk2077"},"mods":[]}""");
        }

        var local = await LibraryService.AddLocalFile(zip);

        // Tag the imported archive as a NexusModsCollectionLibraryFile, the same way a real collection
        // download is tagged by NexusModsLibrary.AddCollectionToDatabase — without hitting the network.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(local.AsLibraryFile().Id, NexusModsCollectionLibraryFile.CollectionSlug, CollectionSlug.From("test-slug"));
            tx.Add(local.AsLibraryFile().Id, NexusModsCollectionLibraryFile.CollectionRevisionNumber, RevisionNumber.From(1));
            await tx.Commit();
        }

        var sourceCollection = NexusModsCollectionLibraryFile.Load(Connection.Db, local.AsLibraryFile().Id);
        sourceCollection.IsValid().Should().BeTrue();

        var jsonEntry = NexusModsLibrary.GetCollectionJsonFile(sourceCollection);
        jsonEntry.IsValid().Should().BeTrue();
        ((LooseFileStore)FileStore).PathFor(jsonEntry.AsLibraryFile().Hash).Delete();
        (await FileStore.HaveFile(jsonEntry.AsLibraryFile().Hash)).Should().BeFalse();

        // collection.json is gone from the store, so parsing must fail before restoring.
        var parseBeforeRestore = async () => await NexusModsLibrary.ParseCollectionJsonFile(sourceCollection, CancellationToken.None);
        await parseBeforeRestore.Should().ThrowAsync<Exception>();

        // This is the same restore call InstallCollectionJob now makes before ParseCollectionJsonFile.
        var reExtractor = ServiceProvider.GetRequiredService<IDownloadReExtractor>();
        var missing = LibraryArchiveFileEntry.FindByParent(Connection.Db, sourceCollection.AsLibraryFile().Id)
            .Select(e => e.AsLibraryFile().Hash)
            .Where(h => !FileStore.HaveFile(h).Result)
            .ToArray();
        missing.Should().Contain(jsonEntry.AsLibraryFile().Hash);
        await reExtractor.RestoreAsync(missing, CancellationToken.None);

        (await FileStore.HaveFile(jsonEntry.AsLibraryFile().Hash)).Should().BeTrue();

        var root = await NexusModsLibrary.ParseCollectionJsonFile(sourceCollection, CancellationToken.None);
        root.Info.Name.Should().Be("Test Collection");
    }
}
