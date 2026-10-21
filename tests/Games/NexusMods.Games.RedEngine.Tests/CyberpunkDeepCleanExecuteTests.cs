using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.DataModel.Storage;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;
using Xunit;

namespace NexusMods.Games.RedEngine.Tests;

public class CyberpunkDeepCleanExecuteTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CyberpunkDeepCleanExecuteTests>(helper)
{
    private readonly AbsolutePath _backups = NexusMods.Paths.FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"cp-backups-{Guid.NewGuid():N}");

    [Fact]
    public async Task Execute_MovesModsToTheInjectedBackups_RemovesGroups_PrunesOlderBackups()
    {
        try { await RunExecute(); }
        finally { if (_backups.DirectoryExists()) _backups.DeleteDirectoryNoFollow(); }
    }

    private async Task RunExecute()
    {
        var tool = ServiceProvider.GetServices<ITool>().OfType<CyberpunkDeepCleanTool>().Single();
        tool.BackupsRootProvider = () => _backups;

        var realBackups = GameBackups.ForGame(FileSystem, Cyberpunk2077Game.GameId);
        var realBefore = Snapshots(realBackups);

        _backups.Combine("20200101_000000").CreateDirectory();
        _backups.Combine("20210101_000000").CreateDirectory();

        var loadout = await CreateLoadout();
        var modFile = GameInstallation.Locations[LocationId.Game].Path.Combine("archive/pc/mod/test.archive");
        modFile.Parent.CreateDirectory();
        await modFile.WriteAllTextAsync("mod");
        var log = new GamePath(LocationId.Game, "r6/logs/redscript_rCURRENT.log");
        GameInstallation.Locations.ToAbsolutePath(log).Parent.CreateDirectory();
        await GameInstallation.Locations.ToAbsolutePath(log).WriteAllTextAsync("log");
        using (var tx = Connection.BeginTransaction())
        {
            AddEmptyGroup(tx, loadout, "PluginMod");
            AddFile(tx, loadout, LoadoutItemGroupId.From(LoadoutOverrides.GetOrCreate(tx, loadout).Value), log);
            await tx.Commit();
        }

        await tool.Execute(Refresh(loadout), CancellationToken.None);

        modFile.FileExists.Should().BeFalse();
        var snapshots = Snapshots(_backups);
        snapshots.Should().HaveCount(2).And.Contain("20210101_000000");
        var newBackup = _backups.Combine(snapshots.Single(s => s != "20210101_000000"));
        newBackup.Combine("archive/pc/mod/test.archive").FileExists.Should().BeTrue();
        LoadoutItem.FindByLoadout(Connection.Db, loadout).Should().NotContain(i => i.Name == "PluginMod");
        // The game's log went to the backup with r6/logs: its External Change must not stay behind
        LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Should().NotContain(f => (GamePath)f.TargetPath == log);
        Snapshots(realBackups).Should().BeEquivalentTo(realBefore);
    }

    [Fact]
    public async Task SuperClean_RunsWhenTheApplyBeforeItFails()
    {
        // Seen 2026-10-14: REDmod's deploy exited 1 at the end of the apply and Super Clean never started. A mod
        // writing through a symlinked folder makes the same apply fail here.
        try { await RunSuperCleanAfterFailedApply(); }
        finally { if (_backups.DirectoryExists()) _backups.DeleteDirectoryNoFollow(); }
    }

    private async Task RunSuperCleanAfterFailedApply()
    {
        var tool = ServiceProvider.GetServices<ITool>().OfType<CyberpunkDeepCleanTool>().Single();
        tool.BackupsRootProvider = () => _backups;

        await LoadoutManager.ManageInstallation(GameInstallation);
        await Synchronizer.ReindexState(GameInstallation);
        var loadout = await Synchronizer.Synchronize(await CreateLoadout());

        var gameRoot = GameInstallation.Locations[LocationId.Game].Path;
        var link = gameRoot.Combine("bin/linked");
        link.Parent.CreateDirectory();
        File.CreateSymbolicLink(link.ToString(), TemporaryFileManager.CreateFolder().Path.ToString());
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)"bin/linked/mod.txt"], loadout, "LinkedMod");
            await tx.Commit();
        }
        var leftover = gameRoot.Combine("archive/pc/mod/test.archive");
        leftover.Parent.CreateDirectory();
        await leftover.WriteAllTextAsync("mod");

        await ServiceProvider.GetRequiredService<IStorageAnalyzer>().RunDeepCleanOnAllLoadoutsAsync();

        leftover.FileExists.Should().BeFalse();
        Snapshots(_backups).Should().ContainSingle()
            .Which.Should().Match(s => _backups.Combine(s).Combine("archive/pc/mod/test.archive").FileExists);
        LoadoutItem.FindByLoadout(Connection.Db, loadout).Should().NotContain(i => i.Name == "LinkedMod");
    }

    private static string[] Snapshots(AbsolutePath root) =>
        root.DirectoryExists() ? Directory.GetDirectories(root.ToString()).Select(d => Path.GetFileName(d)!).ToArray() : [];
}
