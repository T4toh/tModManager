using FluentAssertions;
using NexusMods.Paths;
using NexusMods.Sdk.IO;

namespace NexusMods.DataModel.Tests;

public class ArchiveLocationValidationTests : IDisposable
{
    private readonly IFileSystem _fs = FileSystem.Shared;
    private readonly AbsolutePath _temp = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"storage-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (_temp.DirectoryExists()) _temp.DeleteDirectoryNoFollow();
    }

    [Theory]
    [InlineData(KnownPath.HomeDirectory)]
    [InlineData(KnownPath.XDG_DATA_HOME)]
    [InlineData(KnownPath.XDG_CONFIG_HOME)]
    [InlineData(KnownPath.XDG_CACHE_HOME)]
    [InlineData(KnownPath.XDG_STATE_HOME)]
    public void UserRootsAreRejected(KnownPath knownPath)
    {
        DataModelSettings.ArchiveLocationProblem(_fs.GetKnownPath(knownPath), _fs).Should().NotBeNull();
    }

    [Fact]
    public void FileSystemRootIsRejected()
    {
        DataModelSettings.ArchiveLocationProblem(_fs.FromUnsanitizedFullPath("/"), _fs).Should().NotBeNull();
    }

    [Fact]
    public void SteamLibrariesAreRejected()
    {
        var library = _temp.Combine("SteamLibrary");
        var game = library.Combine("steamapps/common/Cyberpunk 2077");
        game.CreateDirectory();

        DataModelSettings.ArchiveLocationProblem(library, _fs).Should().NotBeNull();
        DataModelSettings.ArchiveLocationProblem(library.Combine("steamapps"), _fs).Should().NotBeNull();
        DataModelSettings.ArchiveLocationProblem(game, _fs).Should().NotBeNull();
    }

    [Fact]
    public void OwnFoldersAreAccepted()
    {
        DataModelSettings.ArchiveLocationProblem(_temp.Combine("tModStore"), _fs).Should().BeNull();
        DataModelSettings.ArchiveLocationProblem(_fs.GetKnownPath(KnownPath.HomeDirectory).Combine("Mods"), _fs).Should().BeNull();
        DataModelSettings.ArchiveLocationProblem(DataModelSettings.GetStandardDataModelFolder(_fs).Combine("Archives"), _fs).Should().BeNull();
    }
}
