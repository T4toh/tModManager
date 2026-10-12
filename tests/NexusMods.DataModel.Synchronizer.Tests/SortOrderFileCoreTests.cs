using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// The core writes every <see cref="ISortOrderFile"/> registered for the game as an intrinsic, with no
/// game code involved: this fake renders the names of the loadout's enabled mod groups.
/// </summary>
public class SortOrderFileCoreTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<SortOrderFileCoreTests>(helper)
{
    private sealed class GroupNamesFile : ISortOrderFile
    {
        public IEnumerable<GameId> GameIds => [Cyberpunk2077Game.GameId];
        public GamePath Path => new(LocationId.Game, "order.txt");
        public string Render(Loadout.ReadOnly loadout, IDb db) =>
            string.Join("\n", LoadoutItemGroup.All(db)
                .Where(g => g.AsLoadoutItem().LoadoutId == loadout.LoadoutId && g.AsLoadoutItem().IsEnabled())
                .Select(g => g.AsLoadoutItem().Name).Order());
    }

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddSingleton<ISortOrderFile, GroupNamesFile>();

    private AbsolutePath OrderFile => GameInstallation.Locations[LocationId.Game].Path.Combine("order.txt");

    [Fact]
    public async Task EverySortOrderFileOfTheGame_IsWrittenOnApply()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        await Synchronizer.ReindexState(GameInstallation);
        var loadout = await Synchronizer.Synchronize(await CreateLoadout());
        using (var tx = Connection.BeginTransaction())
        {
            AddEmptyGroup(tx, loadout.LoadoutId, "Beta");
            AddEmptyGroup(tx, loadout.LoadoutId, "Alpha");
            await tx.Commit();
        }
        Refresh(ref loadout);

        await Synchronizer.Synchronize(loadout);

        OrderFile.FileExists.Should().BeTrue("the core declared the file from DI");
        (await OrderFile.ReadAllTextAsync()).Should().Contain("Alpha\nBeta");
    }
}
