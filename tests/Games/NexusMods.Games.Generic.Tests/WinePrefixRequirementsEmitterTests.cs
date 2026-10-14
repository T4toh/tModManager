using System.Collections.Immutable;
using FluentAssertions;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NSubstitute;
using Xunit;

namespace NexusMods.Games.Generic.Tests;

public class WinePrefixRequirementsEmitterTests
{
    private static readonly WinePrefixRequirements Requirements = new(
        DllOverrides: [new WineDllOverride("winmm", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])],
        WinetricksPackages: ["vcrun2022"]);

    private static readonly DiagnosticId DllOverridesId = new(WinePrefixDiagnostics.Source, number: 1);
    private static readonly DiagnosticId PackagesId = new(WinePrefixDiagnostics.Source, number: 2);

    private static GameInstallation Installation(ILinuxCompatabilityDataProvider? linuxCompat)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Juego de prueba");
        var locatorResult = new GameLocatorResult
        {
            Game = game,
            Path = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory),
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [],
            Locator = Substitute.For<IGameLocator>(),
            LinuxCompatabilityDataProvider = linuxCompat,
        };
        return new GameInstallation(locatorResult, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty));
    }

    private static ILinuxCompatabilityDataProvider Prefix(ImmutableArray<WineDllOverride> overrides, ImmutableHashSet<string> packages)
    {
        var prefix = Substitute.For<ILinuxCompatabilityDataProvider>();
        prefix.GetWineDllOverrides(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(overrides));
        prefix.GetInstalledWinetricksComponents(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(packages));
        return prefix;
    }

    private static async Task<List<Diagnostic>> Run(GameInstallation installation, WinePrefixRequirements requirements)
    {
        var result = new List<Diagnostic>();
        await foreach (var diagnostic in WinePrefixRequirementsEmitter.Diagnose(installation, requirements, CancellationToken.None))
            result.Add(diagnostic);
        return result;
    }

    [Fact]
    public async Task NothingWithoutAPrefix()
    {
        (await Run(Installation(linuxCompat: null), Requirements)).Should().BeEmpty();
    }

    [Fact]
    public async Task NothingWhenTheRequirementsAreEmpty()
    {
        var prefix = Prefix([], []);
        (await Run(Installation(prefix), new WinePrefixRequirements([], []))).Should().BeEmpty();
    }

    [Fact]
    public async Task NothingWhenEverythingIsThere()
    {
        var prefix = Prefix([new WineDllOverride("WINMM", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])], ["vcrun2022", "corefonts"]);
        (await Run(Installation(prefix), Requirements)).Should().BeEmpty();
    }

    [Fact]
    public async Task BothWhenEverythingIsMissing()
    {
        var diagnostics = await Run(Installation(Prefix([], [])), Requirements);
        diagnostics.Select(d => d.Id).Should().Equal(DllOverridesId, PackagesId);
    }

    [Fact]
    public async Task OverrideWithWrongTypesIsReported()
    {
        var prefix = Prefix([new WineDllOverride("winmm", [WineDllOverrideType.BuiltIn])], ["vcrun2022"]);
        var diagnostics = await Run(Installation(prefix), Requirements);
        diagnostics.Select(d => d.Id).Should().Equal(DllOverridesId);
    }

    [Fact]
    public async Task DiagnosticNamesTheGame()
    {
        var diagnostics = await Run(Installation(Prefix([], [])), Requirements);
        diagnostics.Select(d => d switch
        {
            Diagnostic<WinePrefixDiagnostics.RequiredWineDllOverridesMessageData> o => o.MessageData.Game,
            Diagnostic<WinePrefixDiagnostics.RequiredWinetricksPackagesMessageData> p => p.MessageData.Game,
            _ => null,
        }).Should().Equal("Juego de prueba", "Juego de prueba");
    }
}
