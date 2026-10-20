using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Games;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using NSubstitute;
using Xunit;

namespace NexusMods.Games.RedEngine.Tests;

/// <summary>REDmod deploy goes through <see cref="IWineToolRunner"/>: what it runs, when it skips, when it fails.</summary>
public class RedModDeployToolRunnerTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<RedModDeployToolRunnerTests>(helper)
{
    protected override bool WithWinePrefix => true;

    private readonly IWineToolRunner _runner = Substitute.For<IWineToolRunner>();

    private RedModDeployTool Tool()
    {
        _runner.RunAsync(Arg.Any<GameInstallation>(), Arg.Any<AbsolutePath>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<AbsolutePath?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new CommandResult(0, DateTimeOffset.Now, DateTimeOffset.Now));
        return new RedModDeployTool(_runner, NullLogger<RedModDeployTool>.Instance);
    }

    private static StubbedLinuxCompatabilityDataProvider Stub(Loadout.ReadOnly loadout) =>
        (StubbedLinuxCompatabilityDataProvider)loadout.InstallationInstance.LocatorResult.LinuxCompatabilityDataProvider!;

    private static AbsolutePath WriteModlist(Loadout.ReadOnly loadout)
    {
        var modlist = Stub(loadout).WinePrefixDirectoryPath.Combine("drive_c/modlist.txt");
        modlist.Parent.CreateDirectory();
        File.WriteAllText(modlist.ToString(), "tMM_TestRedmod_A\r\n");
        return modlist;
    }

    [Fact]
    public async Task SkipsWithoutARuntime()
    {
        var loadout = await CreateLoadout();
        WriteModlist(loadout);

        await Tool().Execute(loadout, CancellationToken.None);

        await _runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default!, default, default, default);
    }

    [Fact]
    public async Task SkipsWithoutAModlist()
    {
        var loadout = await CreateLoadout();
        Stub(loadout).Runtime = new WineRuntime.SteamProton(1091500);

        await Tool().Execute(loadout, CancellationToken.None);

        await _runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default!, default, default, default);
    }

    [Fact]
    public async Task RunsRedModFromItsFolderWithTheModlist()
    {
        var loadout = await CreateLoadout();
        Stub(loadout).Runtime = new WineRuntime.SteamProton(1091500);
        WriteModlist(loadout);
        var gameDir = loadout.InstallationInstance.LocatorResult.Path;

        await Tool().Execute(loadout, CancellationToken.None);

        await _runner.Received(1).RunAsync(
            Arg.Any<GameInstallation>(),
            Arg.Is<AbsolutePath>(p => p == gameDir.Combine("tools/redmod/bin/redMod.exe")),
            Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "deploy", "-force", @"-modlist=C:\modlist.txt" })),
            null,
            true,
            Arg.Any<CancellationToken>());
        gameDir.Combine("r6/cache/modded").DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public async Task FailsWhenRedModExitsNonZero()
    {
        var loadout = await CreateLoadout();
        Stub(loadout).Runtime = new WineRuntime.SteamProton(1091500);
        WriteModlist(loadout);
        var tool = Tool();
        _runner.RunAsync(Arg.Any<GameInstallation>(), Arg.Any<AbsolutePath>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<AbsolutePath?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new CommandResult(1, DateTimeOffset.Now, DateTimeOffset.Now));

        var act = () => tool.Execute(loadout, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*REDmod*1*");
    }
}
