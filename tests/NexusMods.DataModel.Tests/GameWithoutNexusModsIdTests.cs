using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace NexusMods.DataModel.Tests;

/// <summary>
/// Nexus Mods is one place to get mods from, not what identifies a game: a game without a Nexus page must be
/// addable and manageable next to one that has it.
/// </summary>
public class GameWithoutNexusModsIdTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<GameWithoutNexusModsIdTests>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services)
            .AddGame<StubbedGame>()
            .AddUniversalGameLocator<StubbedGame>(new Version("1.0"));

    [Fact]
    public async Task GetsALoadoutNextToCyberpunk()
    {
        StubbedGame.NexusModsGameId.HasValue.Should().BeFalse();
        var stubbed = GameRegistry.LocateGameInstallations().First(installation => installation.Game is StubbedGame);

        var stubbedLoadout = await LoadoutManager.CreateLoadout(stubbed);
        var cyberpunkLoadout = await CreateLoadout();

        stubbedLoadout.InstallationInstance.Game.Should().BeOfType<StubbedGame>();
        cyberpunkLoadout.InstallationInstance.Game.Should().BeOfType<Cyberpunk2077Game>();
    }

    [Fact]
    public async Task CanBeAddedManually()
    {
        var added = await StubbedGame.Create(ServiceProvider);

        added.LocatorResult.Store.Should().Be(GameStore.ManuallyAdded);
        added.Game.Should().BeOfType<StubbedGame>();
    }

    [Fact]
    public async Task ManualPrefixStaysDeclaredWhenItsFolderIsGone()
    {
        // A Lutris prefix deleted by hand: the DB keeps WinePrefix paths, so the location must stay declared
        var path = TemporaryFileManager.CreateFolder().Path;
        var prefix = path.Combine("pfx");
        using (var tx = Connection.BeginTransaction())
        {
            _ = new ManuallyAddedGame.New(tx)
            {
                Path = path.ToString(),
                Version = "1.0.0.0",
                GameId = StubbedGame.GameId,
                WinePrefix = prefix.ToString(),
            };
            await tx.Commit();
        }
        GameRegistry.ClearCache();

        var added = GameRegistry.LocateGameInstallations().First(g => g.LocatorResult.Path == path);

        prefix.DirectoryExists().Should().BeFalse();
        added.LocatorResult.LinuxCompatabilityDataProvider!.WinePrefixDirectoryPath.Should().Be(prefix);
    }
}
