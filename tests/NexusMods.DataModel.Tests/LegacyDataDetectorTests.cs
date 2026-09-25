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
        LegacyDataDetector.ResetIfRequested(_root, _root.Combine("db"), fs).Should().BeFalse();
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
        var fs = FileSystem.Shared;
        var dataDirectoryRoot = fs.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine(NexusMods.Sdk.ApplicationConstants.DataDirectoryName);
        var markerPath = _root.Combine("reset-marker");
        LegacyDataDetector.RequestResetOnStart(fs, markerPath);

        var act = () => LegacyDataDetector.ResetIfRequested(_root, dataDirectoryRoot, fs, markerPath);

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
        var act = () => LegacyDataDetector.ResetIfRequested(archivesRoot, _root, fs, markerPath);

        act.Should().Throw<InvalidOperationException>();
        archivesRoot.DirectoryExists().Should().BeTrue();
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
}
