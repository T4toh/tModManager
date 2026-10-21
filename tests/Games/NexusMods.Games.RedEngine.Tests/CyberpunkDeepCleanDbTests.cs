using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.RedEngine.Tests;

public class CyberpunkDeepCleanDbTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CyberpunkDeepCleanDbTests>(helper)
{
    [Fact]
    public async Task RemoveModGroups_EmptiesMyModsInsteadOfDeletingIt()
    {
        var loadout = await CreateLoadout();
        var myMods = LoadoutItemGroupId.From(loadout.MutableCollections().Single().CollectionId);

        using (var tx = Connection.BeginTransaction())
        {
            AddEmptyGroup(tx, loadout.LoadoutId, "mod in My Mods", myMods);
            AddEmptyGroup(tx, loadout.LoadoutId, "top-level mod");
            _ = new CollectionGroup.New(tx, out var nexusId)
            {
                IsReadOnly = true,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, nexusId)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, nexusId) { Name = "Nexus collection", LoadoutId = loadout.LoadoutId },
                },
            };
            AddEmptyGroup(tx, loadout.LoadoutId, "mod in Nexus collection", LoadoutItemGroupId.From(nexusId));
            await tx.Commit();
        }

        await CyberpunkDeepCleanTool.RemoveModGroups(Connection, loadout.LoadoutId, [], NullLogger.Instance);

        loadout = Refresh(loadout);
        loadout.MutableCollections().Select(c => c.CollectionId).Should().Equal(myMods.Value);
        var remaining = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId)
            .OfTypeLoadoutItemGroup()
            .Where(g => !new[] { g }.OfTypeLoadoutOverridesGroup().Any())
            .Select(g => g.AsLoadoutItem().Name);
        remaining.Should().Equal("My Mods");
    }

    [Fact]
    public async Task RemoveModGroups_DropsExternalChangesOfWhatTheCleanTookAway()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            var overrides = LoadoutItemGroupId.From(LoadoutOverrides.GetOrCreate(tx, loadout).Value);
            AddFile(tx, loadout.LoadoutId, overrides, new GamePath(LocationId.Game, "bin/x64/plugins/cyber_engine_tweaks/cyber_engine_tweaks.log"));
            AddFile(tx, loadout.LoadoutId, overrides, new GamePath(LocationId.Game, "bin/x64/winmm.dll"));
            AddFile(tx, loadout.LoadoutId, overrides, new GamePath(LocationId.Game, "bin/x64/plugins_readme.txt"));
            AddFile(tx, loadout.LoadoutId, overrides, new GamePath(LocationId.Game, "engine/config/platform/pc/user.ini"));
            AddFile(tx, loadout.LoadoutId, overrides, new GamePath(LocationId.Saves, "bin/x64/plugins/save.dat"));
            await tx.Commit();
        }

        await CyberpunkDeepCleanTool.RemoveModGroups(Connection, loadout.LoadoutId, ["bin/x64/plugins", "bin/x64/winmm.dll"], NullLogger.Instance);

        var left = LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id)
            .SelectMany(group => LoadoutItem.FindByParent(Connection.Db, group.Id).OfTypeLoadoutItemWithTargetPath())
            .Select(file => ((GamePath)file.TargetPath).ToString());
        left.Should().BeEquivalentTo(
            new GamePath(LocationId.Game, "bin/x64/plugins_readme.txt").ToString(),
            new GamePath(LocationId.Game, "engine/config/platform/pc/user.ini").ToString(),
            new GamePath(LocationId.Saves, "bin/x64/plugins/save.dat").ToString());
    }

    private AbsolutePath GameRoot => GameInstallation.Locations[LocationId.Game].Path;

    private async Task Touch(string rel)
    {
        var path = GameRoot.Combine(rel);
        path.Parent.CreateDirectory();
        await path.WriteAllTextAsync(rel);
    }

    // Original files on disk before managing, a sync builds the vanilla list, then a mod drops loose files by hand
    private async Task<GameInstallMetadata.ReadOnly> ListedInstallWithLooseModFiles()
    {
        await Touch("REDprelauncher.exe");
        await Touch("engine/config/platform/pc/user.ini");
        await LoadoutManager.ManageInstallation(GameInstallation);
        await Synchronizer.Synchronize(await CreateLoadout());
        await Touch("FlatlinedExit_readme.txt");
        await Touch("r6/cache/final.redscripts.bk");
        return GameRegistry.ForceGetMetadata(GameInstallation);
    }

    [Fact]
    public async Task LooseModFiles_WithTheList_MovesOnlyFilesNotInIt()
    {
        var metadata = await ListedInstallWithLooseModFiles();

        var found = CyberpunkDeepCleanTool.LooseModFilesToMove(GameRoot, metadata, NullLogger.Instance).Select(p => p.ToString());

        found.Should().BeEquivalentTo("FlatlinedExit_readme.txt", "r6/cache/final.redscripts.bk");
    }

    [Fact]
    public async Task LooseModFiles_WithoutTheList_MovesNothing()
    {
        var metadata = await ListedInstallWithLooseModFiles();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Retract(metadata.Id, GameInstallMetadata.BaselineFromDisk, GameInstallMetadata.BaselineFromDisk.Get(metadata));
            await tx.Commit();
        }

        var found = CyberpunkDeepCleanTool.LooseModFilesToMove(GameRoot, GameRegistry.ForceGetMetadata(GameInstallation), NullLogger.Instance);

        found.Should().BeEmpty();
    }
}
