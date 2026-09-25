using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.DataModel.LegacyData;
using NexusMods.DataModel.Storage;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Settings;
using Xunit;

namespace NexusMods.DataModel.Tests;

public class LegacyDataDetectorTests : IDisposable
{
    private readonly AbsolutePath _root = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"legacy-{Guid.NewGuid():N}");
    public void Dispose() { if (_root.DirectoryExists()) _root.DeleteDirectory(true); }

    [Fact]
    public void Detector_WithNx_ReturnsTrue()
    {
        _root.CreateDirectory();
        _root.Combine("3f2a.nx").Create().Dispose();
        LegacyDataDetector.HasNxArchives(_root).Should().BeTrue();
    }

    [Fact]
    public void Detector_NoNx_ReturnsFalse_EvenWithLegacyDownloads()
    {
        _root.Combine("ab").CreateDirectory();
        _root.Combine("ab/abcdef0123456789").Create().Dispose();
        LegacyDataDetector.HasNxArchives(_root).Should().BeFalse();
    }

    [Fact]
    public void Detector_MissingFolder_ReturnsFalse() =>
        LegacyDataDetector.HasNxArchives(_root.Combine("nope")).Should().BeFalse();

    [Fact]
    public void ResetIfRequested_WithoutMarker_DoesNothing()
    {
        _root.CreateDirectory();
        _root.Combine("a.nx").Create().Dispose();
        var fs = new InMemoryFileSystem();
        // Explicit temp markerPath/dataDirectoryRoot: fs is already an isolated in-memory
        // filesystem (never touches real disk on its own), but every call here is spelled out so
        // none of this test's arguments could ever be mistaken for, or accidentally fall back to,
        // a real path.
        var markerPath = _root.Combine("reset-marker");
        var dataDirectoryRoot = _root.Combine("data-root");
        LegacyDataDetector.ResetIfRequested(_root, _root.Combine("db"), fs, markerPath, dataDirectoryRoot).Should().BeFalse();
        _root.Combine("a.nx").FileExists.Should().BeTrue();
    }

    [Fact]
    public void ResetIfRequested_WithMarker_DeletesNxAndDbOnly()
    {
        // Create a real temp folder structure
        _root.CreateDirectory();

        // Create .nx file (should be deleted)
        _root.Combine("a.nx").Create().Dispose();

        // Create new-store hex subfolder file (should survive)
        _root.Combine("AB").CreateDirectory();
        _root.Combine("AB/AB00000000000000").Create().Dispose();

        // Create DB directory with a file (should be deleted)
        var dbPath = _root.Combine("db");
        dbPath.CreateDirectory();
        dbPath.Combine("data.rocksdb").Create().Dispose();

        // Create marker at a temp location (test seam)
        var markerPath = _root.Combine("reset-marker");
        var fs = FileSystem.Shared;
        LegacyDataDetector.RequestResetOnStart(fs, markerPath);

        // Verify marker was created
        markerPath.FileExists.Should().BeTrue();

        // Call reset
        var result = LegacyDataDetector.ResetIfRequested(_root, dbPath, fs, markerPath);

        // Verify reset happened
        result.Should().BeTrue();

        // .nx file should be deleted
        _root.Combine("a.nx").FileExists.Should().BeFalse();

        // DB directory should be deleted
        dbPath.DirectoryExists().Should().BeFalse();

        // New-store file should survive
        _root.Combine("AB/AB00000000000000").FileExists.Should().BeTrue();

        // Marker should be deleted
        markerPath.FileExists.Should().BeFalse();
    }

    [Fact]
    public void IsResetPending_ReflectsMarker()
    {
        var fs = FileSystem.Shared;
        var markerPath = _root.Combine("reset-marker");

        LegacyDataDetector.IsResetPending(fs, markerPath).Should().BeFalse();

        LegacyDataDetector.RequestResetOnStart(fs, markerPath);
        LegacyDataDetector.IsResetPending(fs, markerPath).Should().BeTrue();
    }

    [Fact]
    public void ResetIfRequested_RefusesWhenMnemonicDbPathIsDataDirectoryRoot()
    {
        // The "data directory root" is entirely a temp folder here (via the dataDirectoryRoot
        // override): even if this guard regresses, nothing but this test's own temp folder is at
        // risk — never the developer's real ~/.local/share/tModManager.
        _root.CreateDirectory();
        var dataDirectoryRoot = _root.Combine("data-root");
        var markerPath = _root.Combine("reset-marker");
        var fs = FileSystem.Shared;
        LegacyDataDetector.RequestResetOnStart(fs, markerPath);

        var act = () => LegacyDataDetector.ResetIfRequested(_root.Combine("Archives"), dataDirectoryRoot, fs, markerPath, dataDirectoryRoot);

        act.Should().Throw<InvalidOperationException>();
        // Refused, not consumed: a fixed call can retry the reset later.
        markerPath.FileExists.Should().BeTrue();
    }

    [Fact]
    public void ResetIfRequested_RefusesWhenMnemonicDbPathIsAncestorOfArchivesRoot()
    {
        _root.CreateDirectory();
        var archivesRoot = _root.Combine("Archives");
        archivesRoot.CreateDirectory();
        var markerPath = _root.Combine("reset-marker");
        var fs = FileSystem.Shared;
        LegacyDataDetector.RequestResetOnStart(fs, markerPath);

        // _root is an ancestor of archivesRoot: passing it as the DB path must be refused.
        var act = () => LegacyDataDetector.ResetIfRequested(archivesRoot, _root, fs, markerPath, dataDirectoryRoot: _root.Combine("data-root"));

        act.Should().Throw<InvalidOperationException>();
        archivesRoot.DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public void ResetIfRequested_RefusesWhenMnemonicDbPathEscapesIntoArchivesRootViaDotDot()
    {
        // NexusMods.Paths keeps "x/.." unnormalized, so a naive segment/string comparison of the raw
        // paths misses that "Archives/DataModel/.." IS "Archives" on disk. The guard must resolve
        // real paths (Path.GetFullPath collapses "..") before comparing.
        _root.CreateDirectory();
        var archivesRoot = _root.Combine("Archives");
        var nested = archivesRoot.Combine("DataModel");
        nested.CreateDirectory();
        var mnemonicDbPath = nested.Combine("..");

        var markerPath = _root.Combine("reset-marker");
        var fs = FileSystem.Shared;
        LegacyDataDetector.RequestResetOnStart(fs, markerPath);

        var act = () => LegacyDataDetector.ResetIfRequested(archivesRoot, mnemonicDbPath, fs, markerPath, dataDirectoryRoot: _root.Combine("data-root"));

        act.Should().Throw<InvalidOperationException>();
        archivesRoot.DirectoryExists().Should().BeTrue();
        nested.DirectoryExists().Should().BeTrue();
    }

    [Theory]
    // No marker: pass-through, unaffected by whether a reset was ever considered.
    [InlineData(true, false, false, true)] // existing DB, no reset requested -> MigrateAll
    [InlineData(false, false, false, false)] // fresh install, no reset requested -> InitialSetup
    // Marker present and the reset actually ran (ResetIfRequested cleared it after deleting the DB
    // dir): the database is now empty regardless of whether it existed before -> InitialSetup.
    [InlineData(true, true, false, false)]
    // Marker present but the reset was refused by its own safety guard (still pending afterwards):
    // nothing was deleted, the existing DB must still be migrated, not re-initialized (which would
    // throw "already has a schema version").
    [InlineData(true, true, true, true)]
    public void ModelExistsAfterReset_DecidesInitialSetupVsMigrate(
        bool dirExistedBeforeReset, bool resetWasPendingBefore, bool resetWasPendingAfter, bool expected)
    {
        LegacyDataDetector.ModelExistsAfterReset(dirExistedBeforeReset, resetWasPendingBefore, resetWasPendingAfter)
            .Should().Be(expected);
    }
}

public class LegacyDownloadsMoveTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<LegacyDownloadsMoveTests>(helper)
{
    [Fact]
    public async Task GetLegacyDownloadsAsync_CountsAndSizesFiles()
    {
        var analyzer = (StorageAnalyzer)ServiceProvider.GetRequiredService<IStorageAnalyzer>();
        var legacy = TemporaryFileManager.CreateFolder().Path;
        await File.WriteAllTextAsync(legacy.Combine("A-1-0.zip").ToString(), "aa");
        await File.WriteAllTextAsync(legacy.Combine("B-2-0.7z").ToString(), "b");
        analyzer.LegacyDownloadsFolderProvider = () => legacy;

        var (count, size) = await analyzer.GetLegacyDownloadsAsync();

        count.Should().Be(2);
        size.Value.Should().Be(3);
    }

    [Fact]
    public async Task MoveLegacyDownloads_MovesAndKeepsNames()
    {
        var analyzer = (StorageAnalyzer)ServiceProvider.GetRequiredService<IStorageAnalyzer>();
        var legacy = TemporaryFileManager.CreateFolder().Path;
        await File.WriteAllTextAsync(legacy.Combine("A-1-0.zip").ToString(), "a");
        await File.WriteAllTextAsync(legacy.Combine("B-2-0.7z").ToString(), "b");
        analyzer.LegacyDownloadsFolderProvider = () => legacy;

        var moved = await analyzer.MoveLegacyDownloadsAsync(default);

        moved.Should().Be(2);
        legacy.EnumerateFiles("*", recursive: false).Should().BeEmpty();
        var dest = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);
        dest.Combine("A-1-0.zip").FileExists.Should().BeTrue();
        dest.Combine("B-2-0.7z").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task MoveLegacyDownloads_SameNameSameContent_DiscardsSource()
    {
        var analyzer = (StorageAnalyzer)ServiceProvider.GetRequiredService<IStorageAnalyzer>();
        var dest = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);
        dest.CreateDirectory();
        await File.WriteAllTextAsync(dest.Combine("X.zip").ToString(), "same content");

        var legacy = TemporaryFileManager.CreateFolder().Path;
        await File.WriteAllTextAsync(legacy.Combine("X.zip").ToString(), "same content");
        analyzer.LegacyDownloadsFolderProvider = () => legacy;

        var moved = await analyzer.MoveLegacyDownloadsAsync(default);

        moved.Should().Be(1);
        legacy.EnumerateFiles("*", recursive: false).Should().BeEmpty();
        dest.EnumerateFiles("*", recursive: false).Should().ContainSingle(f => f.FileName.ToString() == "X.zip");
    }

    [Fact]
    public async Task MoveLegacyDownloads_SameNameDifferentContent_GetsSuffix()
    {
        var analyzer = (StorageAnalyzer)ServiceProvider.GetRequiredService<IStorageAnalyzer>();
        var dest = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);
        dest.CreateDirectory();
        await File.WriteAllTextAsync(dest.Combine("X.zip").ToString(), "original content");

        var legacy = TemporaryFileManager.CreateFolder().Path;
        await File.WriteAllTextAsync(legacy.Combine("X.zip").ToString(), "different content");
        analyzer.LegacyDownloadsFolderProvider = () => legacy;

        var moved = await analyzer.MoveLegacyDownloadsAsync(default);

        moved.Should().Be(1);
        legacy.EnumerateFiles("*", recursive: false).Should().BeEmpty();
        dest.Combine("X.zip").FileExists.Should().BeTrue();
        dest.Combine("X_1.zip").FileExists.Should().BeTrue();
        (await File.ReadAllTextAsync(dest.Combine("X.zip").ToString())).Should().Be("original content");
        (await File.ReadAllTextAsync(dest.Combine("X_1.zip").ToString())).Should().Be("different content");
    }

    [Fact]
    public async Task MoveLegacyDownloads_SameFolderConfiguredDirectly_DeletesNothing()
    {
        var analyzer = (StorageAnalyzer)ServiceProvider.GetRequiredService<IStorageAnalyzer>();
        var dest = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);
        dest.CreateDirectory();
        await File.WriteAllTextAsync(dest.Combine("A-1-0.zip").ToString(), "a");

        // The legacy folder IS the current downloads folder (e.g. misconfiguration, or the user
        // pointed both at the same place): there's nothing to move, and treating it as a move would
        // delete the only copy of every file.
        analyzer.LegacyDownloadsFolderProvider = () => dest;

        var (count, _) = await analyzer.GetLegacyDownloadsAsync();
        var moved = await analyzer.MoveLegacyDownloadsAsync(default);

        count.Should().Be(0);
        moved.Should().Be(0);
        dest.Combine("A-1-0.zip").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task MoveLegacyDownloads_DestinationIsSymlinkToLegacyFolder_DeletesNothing()
    {
        var analyzer = (StorageAnalyzer)ServiceProvider.GetRequiredService<IStorageAnalyzer>();
        var legacy = TemporaryFileManager.CreateFolder().Path;
        await File.WriteAllTextAsync(legacy.Combine("A-1-0.zip").ToString(), "a");
        analyzer.LegacyDownloadsFolderProvider = () => legacy;

        var dest = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);
        dest.Parent.CreateDirectory();
        if (dest.DirectoryExists()) dest.DeleteDirectory(recursive: true);
        Directory.CreateSymbolicLink(dest.ToString(), legacy.ToString());

        var (count, _) = await analyzer.GetLegacyDownloadsAsync();
        var moved = await analyzer.MoveLegacyDownloadsAsync(default);

        count.Should().Be(0);
        moved.Should().Be(0);
        legacy.Combine("A-1-0.zip").FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task GetAndMoveLegacyDownloads_SkipPartialDownloads()
    {
        var analyzer = (StorageAnalyzer)ServiceProvider.GetRequiredService<IStorageAnalyzer>();
        var legacy = TemporaryFileManager.CreateFolder().Path;
        await File.WriteAllTextAsync(legacy.Combine("A-1-0.zip").ToString(), "a");
        var partial = legacy.Combine("B-2-0.7z.tmp-0123456789abcdef0123456789abcdef");
        await File.WriteAllTextAsync(partial.ToString(), "half-written");
        analyzer.LegacyDownloadsFolderProvider = () => legacy;

        var (count, size) = await analyzer.GetLegacyDownloadsAsync();
        var moved = await analyzer.MoveLegacyDownloadsAsync(default);

        count.Should().Be(1);
        size.Value.Should().Be(1);
        moved.Should().Be(1);
        partial.FileExists.Should().BeTrue("a half-written download must be left alone, not moved or deleted");
        var dest = ServiceProvider.GetRequiredService<ISettingsManager>().Get<DownloadsSettings>().Folder.ToPath(FileSystem);
        dest.Combine("A-1-0.zip").FileExists.Should().BeTrue();
        dest.Combine(partial.FileName).FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteProtonPrefixAsync_MissingSteamapps_DoesNothing()
    {
        var analyzer = (StorageAnalyzer)ServiceProvider.GetRequiredService<IStorageAnalyzer>();
        var steamLibraryRoot = TemporaryFileManager.CreateFolder().Path;

        await analyzer.DeleteProtonPrefixAsync(steamLibraryRoot);

        steamLibraryRoot.DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public async Task DeleteProtonPrefixAsync_DeletesExpectedPrefix()
    {
        var analyzer = (StorageAnalyzer)ServiceProvider.GetRequiredService<IStorageAnalyzer>();
        var steamLibraryRoot = TemporaryFileManager.CreateFolder().Path;
        var prefix = steamLibraryRoot.Combine("steamapps/compatdata/1091500");
        prefix.CreateDirectory();
        await File.WriteAllTextAsync(prefix.Combine("registry.reg").ToString(), "wine prefix data");

        await analyzer.DeleteProtonPrefixAsync(steamLibraryRoot);

        prefix.DirectoryExists().Should().BeFalse();
        steamLibraryRoot.Combine("steamapps").DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public async Task DeleteProtonPrefixAsync_DoesNotFollowSymlinksOutOfThePrefix()
    {
        // Every Wine prefix ships dosdevices/z: -> /. Following it deletes the user's disk.
        var analyzer = (StorageAnalyzer)ServiceProvider.GetRequiredService<IStorageAnalyzer>();
        var steamLibraryRoot = TemporaryFileManager.CreateFolder().Path;
        var outside = TemporaryFileManager.CreateFolder().Path;
        var canary = outside.Combine("sub/canary.txt");
        canary.Parent.CreateDirectory();
        await File.WriteAllTextAsync(canary.ToString(), "must survive");
        var prefix = steamLibraryRoot.Combine("steamapps/compatdata/1091500");
        prefix.Combine("pfx/dosdevices").CreateDirectory();
        File.CreateSymbolicLink(prefix.Combine("pfx/dosdevices/z:").ToString(), outside.ToString());

        await analyzer.DeleteProtonPrefixAsync(steamLibraryRoot);

        prefix.DirectoryExists().Should().BeFalse();
        canary.FileExists.Should().BeTrue();
    }
}
