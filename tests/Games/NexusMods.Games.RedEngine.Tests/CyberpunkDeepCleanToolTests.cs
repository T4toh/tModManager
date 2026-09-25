using FluentAssertions;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using Xunit;

namespace NexusMods.Games.RedEngine.Tests;

public class CyberpunkDeepCleanToolTests : IDisposable
{
    private readonly AbsolutePath _game = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"cp-{Guid.NewGuid():N}");
    public void Dispose() { if (_game.DirectoryExists()) _game.DeleteDirectory(true); }

    private void Touch(string rel) { var p = _game.Combine(rel); p.Parent.CreateDirectory(); p.Create().Dispose(); }

    [Fact]
    public void LooseFiles_NonVanillaRootAndConfigFiles_AreFound_VanillaKept()
    {
        Touch("FlatlinedExit_readme.txt");
        Touch("REDprelauncher.exe");
        Touch("engine/config/platform/pc/input_loader.ini");
        Touch("engine/config/platform/pc/user.ini");     // vanilla in this test
        Touch("r6/cache/final.redscripts.bk");
        Touch("tools/redmod/tweaks/base/gameplay/static_data/database/devices.tweak");
        Touch("bin/x64/CyberPunk.bat");
        var vanilla = new HashSet<GamePath>
        {
            new(LocationId.Game, "engine/config/platform/pc/user.ini"),
            new(LocationId.Game, "REDprelauncher.exe"),
        };

        var found = CyberpunkDeepCleanTool.FindLooseModFiles(_game, vanilla).Select(p => p.ToString()).ToArray();

        found.Should().BeEquivalentTo(
            "FlatlinedExit_readme.txt",
            "engine/config/platform/pc/input_loader.ini",
            "r6/cache/final.redscripts.bk",
            "tools/redmod/tweaks/base/gameplay/static_data/database/devices.tweak",
            "bin/x64/CyberPunk.bat");
    }

    [Fact]
    public void LooseFiles_VanillaEntryWithDifferentCasing_IsStillKept()
    {
        // Vanilla data comes from Steam depot manifests (Windows casing); the file on a Linux disk
        // may have different casing for the same path. It must never be treated as a mod leftover.
        Touch("engine/config/platform/pc/User.ini");
        var vanilla = new HashSet<GamePath>
        {
            new(LocationId.Game, "engine/config/platform/pc/user.ini"),
        };

        var found = CyberpunkDeepCleanTool.FindLooseModFiles(_game, vanilla);

        found.Should().BeEmpty();
    }

    [Fact]
    public void FindLooseModFiles_EmptyVanillaSet_ReturnsNothing()
    {
        // Safety guard: an empty vanilla set means the file-hash service has no data for this game
        // version (unknown version, no network, etc). Never move files blind in that case.
        Touch("FlatlinedExit_readme.txt");
        Touch("REDprelauncher.exe");

        var found = CyberpunkDeepCleanTool.FindLooseModFiles(_game, new HashSet<GamePath>());

        found.Should().BeEmpty();
    }
}
