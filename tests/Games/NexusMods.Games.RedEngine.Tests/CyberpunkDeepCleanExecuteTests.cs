using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
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
        using (var tx = Connection.BeginTransaction())
        {
            AddEmptyGroup(tx, loadout, "PluginMod");
            await tx.Commit();
        }

        await tool.Execute(Refresh(loadout), CancellationToken.None);

        modFile.FileExists.Should().BeFalse();
        var snapshots = Snapshots(_backups);
        snapshots.Should().HaveCount(2).And.Contain("20210101_000000");
        var newBackup = _backups.Combine(snapshots.Single(s => s != "20210101_000000"));
        newBackup.Combine("archive/pc/mod/test.archive").FileExists.Should().BeTrue();
        LoadoutItem.FindByLoadout(Connection.Db, loadout).Should().NotContain(i => i.Name == "PluginMod");
        Snapshots(realBackups).Should().BeEquivalentTo(realBefore);
    }

    private static string[] Snapshots(AbsolutePath root) =>
        root.DirectoryExists() ? Directory.GetDirectories(root.ToString()).Select(d => Path.GetFileName(d)!).ToArray() : [];
}
