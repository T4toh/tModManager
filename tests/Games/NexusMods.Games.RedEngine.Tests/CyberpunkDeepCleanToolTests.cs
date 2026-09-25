using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public void LooseFiles_VanillaPublishingFile_IsKept_NonVanillaPublishingFile_IsFound()
    {
        // r6/publishing is a mixed directory: the base game ships addonDescriptions.xml under it
        // (confirmed against the Steam depot manifest), so it must be swept file-by-file, not moved
        // wholesale like the other new mod directories.
        Touch("r6/publishing/x64/Steam/additional-content/addonDescriptions.xml"); // vanilla
        Touch("r6/publishing/x64/mod/some_mod_publishing_file.xml");               // mod leftover
        var vanilla = new HashSet<GamePath>
        {
            new(LocationId.Game, "r6/publishing/x64/Steam/additional-content/addonDescriptions.xml"),
        };

        var found = CyberpunkDeepCleanTool.FindLooseModFiles(_game, vanilla).Select(p => p.ToString()).ToArray();

        found.Should().BeEquivalentTo("r6/publishing/x64/mod/some_mod_publishing_file.xml");
    }

    [Fact]
    public void ResolveVanilla_AllIdsKnown_ReturnsUnionOfFiles()
    {
        var idA = LocatorId.From("100");
        var idB = LocatorId.From("200");
        var filesA = new[] { new GamePath(LocationId.Game, "a.txt") };
        var filesB = new[] { new GamePath(LocationId.Game, "b.txt") };

        var result = CyberpunkDeepCleanTool.ResolveVanilla(
            id => id == idA ? filesA : id == idB ? filesB : [],
            [idA, idB]);

        result.UnknownIds.Should().BeEmpty();
        result.Vanilla.Should().BeEquivalentTo(filesA.Concat(filesB));
    }

    [Fact]
    public void ResolveVanilla_OneIdUnknown_ReturnsEmptyAndNamesIt()
    {
        var idKnown = LocatorId.From("100");
        var idUnknown = LocatorId.From("999");
        var filesKnown = new[] { new GamePath(LocationId.Game, "a.txt") };

        var result = CyberpunkDeepCleanTool.ResolveVanilla(
            id => id == idKnown ? filesKnown : [],
            [idKnown, idUnknown]);

        result.Vanilla.Should().BeEmpty();
        result.UnknownIds.Should().BeEquivalentTo([idUnknown]);
    }

    [Fact]
    public void PruneOldBackups_WithoutANewBackup_KeepsEveryBackup()
    {
        _game.Combine("20260101_000000").Combine("mods").CreateDirectory();

        CyberpunkDeepCleanTool.PruneOldBackups(_game, "20260102_000000", backupCreated: false, NullLogger.Instance);

        _game.Combine("20260101_000000").Combine("mods").DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public void PruneOldBackups_AfterANewBackup_KeepsOnlyTheNewOne()
    {
        _game.Combine("20260101_000000").CreateDirectory();
        _game.Combine("20260102_000000").CreateDirectory();

        CyberpunkDeepCleanTool.PruneOldBackups(_game, "20260102_000000", backupCreated: true, NullLogger.Instance);

        _game.Combine("20260101_000000").DirectoryExists().Should().BeFalse();
        _game.Combine("20260102_000000").DirectoryExists().Should().BeTrue();
    }
}
