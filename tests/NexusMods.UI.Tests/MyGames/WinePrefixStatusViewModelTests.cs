using System.Collections.Immutable;
using FluentAssertions;
using NexusMods.App.UI.Pages.MyGames.WinePrefix;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.UI.Tests.MyGames;

public class WinePrefixStatusViewModelTests
{
    [Fact]
    public void MissingProtontricksIsNotAnIssueWithoutPackages()
    {
        WinePrefixStatusViewModel.ComputeHasIssues(isProtontricksInstalled: false, packages: [], hasCorrectDllOverrides: true)
            .Should().BeFalse();
    }

    [Fact]
    public void MissingProtontricksIsAnIssueWhenTheGameNeedsPackages()
    {
        WinePrefixStatusViewModel.ComputeHasIssues(isProtontricksInstalled: false, packages: [new WinetricksPackageStatus("vcrun2022", IsInstalled: true)], hasCorrectDllOverrides: true)
            .Should().BeTrue();
    }

    [Fact]
    public void MissingPackageOrWrongOverridesAreIssues()
    {
        WinePrefixStatusViewModel.ComputeHasIssues(true, [new WinetricksPackageStatus("vcrun2022", IsInstalled: false)], true).Should().BeTrue();
        WinePrefixStatusViewModel.ComputeHasIssues(true, [], hasCorrectDllOverrides: false).Should().BeTrue();
        WinePrefixStatusViewModel.ComputeHasIssues(true, [new WinetricksPackageStatus("vcrun2022", IsInstalled: true)], true).Should().BeFalse();
    }

    [Fact]
    public void TwoInstallationsOfTheSameGame_GetPanelsTheUserCanTellApart()
    {
        var game = Substitute.For<IGameData>();
        var steam = Panel(Installation(game, GameStore.Steam, "/games/steam/Cyberpunk 2077"));
        var manual = Panel(Installation(game, GameStore.ManuallyAdded, "/games/lutris/cyberpunk"));

        steam.Title.Should().Be("Requisitos del prefijo Wine · Steam");
        manual.Title.Should().Be("Requisitos del prefijo Wine · Manually Added");
        steam.GamePath.Should().Be("/games/steam/Cyberpunk 2077");
        manual.GamePath.Should().Be("/games/lutris/cyberpunk");
    }

    private static WinePrefixStatusViewModel Panel(GameInstallation installation) =>
        new(installation, [], Substitute.For<IProcessRunner>());

    private static GameInstallation Installation(IGameData game, GameStore store, string path)
    {
        var gamePath = FileSystem.Shared.FromUnsanitizedFullPath(path);
        var locatorResult = new GameLocatorResult
        {
            Game = game,
            Path = gamePath,
            Store = store,
            StoreIdentifier = "1",
            LocatorIds = [],
            Locator = Substitute.For<IGameLocator>(),
        };
        return new GameInstallation(locatorResult, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, gamePath)));
    }
}
