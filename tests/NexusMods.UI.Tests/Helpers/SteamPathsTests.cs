using FluentAssertions;
using NexusMods.App.UI.Helpers;
using NexusMods.Paths;

namespace NexusMods.UI.Tests.Helpers;

public class SteamPathsTests
{
    [Fact]
    public void FindLibraryRoot_WalksUpToTheFolderWithSteamapps()
    {
        using var tempDir = new TempDir();
        var library = FileSystem.Shared.FromUnsanitizedFullPath(tempDir.Path);
        var game = library.Combine("steamapps").Combine("common").Combine("Cyberpunk 2077");
        game.CreateDirectory();

        SteamPaths.FindLibraryRoot(game).Should().Be(library);
        SteamPaths.FindLibraryRoot(library.Combine("steamapps")).Should().Be(library);
    }

    [Fact]
    public void FindLibraryRoot_ReturnsNullWithoutSteamapps()
    {
        using var tempDir = new TempDir();
        var game = FileSystem.Shared.FromUnsanitizedFullPath(tempDir.Path).Combine("Games").Combine("Cyberpunk 2077");
        game.CreateDirectory();

        SteamPaths.FindLibraryRoot(game).Should().BeNull();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("steam-paths-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
