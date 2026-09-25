using FluentAssertions;
using NexusMods.DataModel.LegacyData;
using NexusMods.Paths;
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
}
