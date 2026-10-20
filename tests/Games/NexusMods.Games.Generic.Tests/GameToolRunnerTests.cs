using System.Collections.Immutable;
using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Games;
using NexusMods.Games.Generic.Dependencies;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;
using NSubstitute;
using Xunit;

namespace NexusMods.Games.Generic.Tests;

public class GameToolRunnerTests : IDisposable
{
    private readonly AbsolutePath _tmp;
    private readonly AbsolutePath _prefix;
    private readonly AbsolutePath _exe;   // under a folder with a space, like "Cyberpunk 2077"
    private readonly StubbedLinuxCompatabilityDataProvider _provider;

    public GameToolRunnerTests()
    {
        _tmp = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"runner-tests-{Guid.NewGuid():N}");
        _prefix = _tmp.Combine("pfx");
        _prefix.CreateDirectory();
        _exe = _tmp.Combine("Juego 2077/tools/redmod/bin/redMod.exe");
        _exe.Parent.CreateDirectory();
        File.WriteAllText(_exe.ToString(), "");
        _provider = new StubbedLinuxCompatabilityDataProvider(_prefix);
    }

    public void Dispose() => _tmp.DeleteDirectoryNoFollow();

    private GameInstallation Installation(bool withProvider = true)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Juego de prueba");
        var locatorResult = new GameLocatorResult
        {
            Game = game,
            Path = _tmp.Combine("Juego 2077"),
            Store = GameStore.ManuallyAdded,
            StoreIdentifier = "1",
            LocatorIds = [],
            Locator = Substitute.For<IGameLocator>(),
            LinuxCompatabilityDataProvider = withProvider ? _provider : null,
        };
        return new GameInstallation(locatorResult, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty));
    }

    private static (GameToolRunner Runner, IProcessRunner Process, IProtontricksDependency Protontricks, Func<Command?> Captured) Make(bool withProtontricks = true)
    {
        Command? captured = null;
        var process = Substitute.For<IProcessRunner>();
        process.RunAsync(Arg.Do<Command>(c => captured = c), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new CommandResult(0, DateTimeOffset.Now, DateTimeOffset.Now));
        var protontricks = Substitute.For<IProtontricksDependency>();
        protontricks.MakeLaunchCommand(Arg.Any<Command>(), Arg.Any<long>())
            .Returns(ci => ValueTask.FromResult(ci.Arg<Command>()));   // pass-through, so the inner command is inspectable
        var runner = new GameToolRunner(process, NullLogger<GameToolRunner>.Instance, withProtontricks ? protontricks : null);
        return (runner, process, protontricks, () => captured);
    }

    [Fact]
    public async Task SteamProtonGoesThroughProtontricks()
    {
        _provider.Runtime = new WineRuntime.SteamProton(1091500);
        var (runner, _, protontricks, captured) = Make();

        await runner.RunAsync(Installation(), _exe, ["deploy", "-force"]);

        await protontricks.Received().MakeLaunchCommand(Arg.Is<Command>(c => c.TargetFilePath == _exe.ToString() && c.WorkingDirPath == _exe.Parent.ToString()), 1091500);
        captured()!.Arguments.Should().Contain("deploy -force");
        captured()!.Validation.Should().Be(CommandResultValidation.None);
    }

    [Fact]
    public async Task TheRealProtontricksCommandQuotesTheExeAndKeepsTheArguments()
    {
        // Review Focus 3: a game folder with a space and the -modlist backslashes reach protontricks-launch intact
        var inner = Cli.Wrap(_exe.ToString()).WithArguments(["deploy", "-force", @"-modlist=C:\modlist.txt"], escape: true).WithWorkingDirectory(_exe.Parent.ToString());

        var launch = await new ProtontricksNativeDependency(Substitute.For<IProcessRunner>()).MakeLaunchCommand(inner, 1091500);

        launch.TargetFilePath.Should().Be("protontricks-launch");
        launch.Arguments.Should().Be($"--appid 1091500 \"{_exe}\" deploy -force -modlist=C:\\modlist.txt");
        launch.WorkingDirPath.Should().Be(_exe.Parent.ToString());
    }

    [Fact]
    public async Task SteamProtonWithoutProtontricksThrows()
    {
        _provider.Runtime = new WineRuntime.SteamProton(1091500);
        var (runner, process, _, _) = Make(withProtontricks: false);

        var act = () => runner.RunAsync(Installation(), _exe, []);

        await act.Should().ThrowAsync<WineRuntimeUnavailableException>().WithMessage("*protontricks*");
        await process.DidNotReceiveWithAnyArgs().RunAsync(default(Command)!, default, default);
    }

    [Fact]
    public async Task WineBuildRunsWineWithPrefixAndEnvironment()
    {
        var runnerDir = _tmp.Combine("runners/wine/wine-ge");
        _provider.Runtime = new WineRuntime.Wine(runnerDir, ImmutableDictionary<string, string>.Empty.Add("FOO", "bar").Add("WINEDLLOVERRIDES", "winmm=n,b"));
        var (runner, _, _, captured) = Make();

        await runner.RunAsync(Installation(), _exe, ["deploy"]);

        var command = captured()!;
        command.TargetFilePath.Should().Be(runnerDir.Combine("bin/wine").ToString());
        command.Arguments.Should().Contain(_exe.ToString()).And.Contain("deploy");
        command.WorkingDirPath.Should().Be(_exe.Parent.ToString());
        command.EnvironmentVariables["WINEPREFIX"].Should().Be(_prefix.ToString());
        command.EnvironmentVariables["FOO"].Should().Be("bar");
        command.EnvironmentVariables["WINEDLLOVERRIDES"].Should().Be("winmm=n,b");
        command.EnvironmentVariables["WINEDEBUG"].Should().Be("-all", "a whole play session of wine fixme: lines must not land in ProcessLogs");
    }

    [Fact]
    public async Task ConfiguredWineDebugIsKept()
    {
        _provider.Runtime = new WineRuntime.Wine(_tmp.Combine("runners/wine/wine-ge"), ImmutableDictionary<string, string>.Empty.Add("WINEDEBUG", "+loaddll"));
        var (runner, _, _, captured) = Make();

        await runner.RunAsync(Installation(), _exe, []);

        captured()!.EnvironmentVariables["WINEDEBUG"].Should().Be("+loaddll");
    }

    [Fact]
    public async Task ProtonBuildRunsUmu()
    {
        var protonDir = _tmp.Combine("runners/wine/GE-Proton7-55");
        _provider.Runtime = new WineRuntime.ProtonViaUmu(protonDir, ImmutableDictionary<string, string>.Empty);
        var (runner, _, _, captured) = Make();

        await runner.RunAsync(Installation(), _exe, ["deploy"]);

        var command = captured()!;
        command.TargetFilePath.Should().EndWith("umu-run");
        command.EnvironmentVariables["PROTONPATH"].Should().Be(protonDir.ToString());
        command.EnvironmentVariables["GAMEID"].Should().Be("umu-default");
        command.EnvironmentVariables["WINEPREFIX"].Should().Be(_prefix.ToString());
    }

    [Fact]
    public async Task ExplicitWorkingDirectoryWins()
    {
        _provider.Runtime = new WineRuntime.Wine(_tmp.Combine("runners/wine/wine-ge"), ImmutableDictionary<string, string>.Empty);
        var (runner, _, _, captured) = Make();

        await runner.RunAsync(Installation(), _exe, [], workingDirectory: _tmp);

        captured()!.WorkingDirPath.Should().Be(_tmp.ToString());
    }

    [Fact]
    public async Task NoRuntimeThrows()
    {
        var (runner, process, _, _) = Make();

        var act = () => runner.RunAsync(Installation(), _exe, []);

        await act.Should().ThrowAsync<WineRuntimeUnavailableException>().WithMessage("*Lutris*");
        await process.DidNotReceiveWithAnyArgs().RunAsync(default(Command)!, default, default);
    }

    [Fact]
    public async Task NoProviderThrows()
    {
        var (runner, process, _, _) = Make();

        var act = () => runner.RunAsync(Installation(withProvider: false), _exe, []);

        await act.Should().ThrowAsync<WineRuntimeUnavailableException>();
        await process.DidNotReceiveWithAnyArgs().RunAsync(default(Command)!, default, default);
    }

    [Fact]
    public async Task MissingExecutableThrowsBeforeRunning()
    {
        _provider.Runtime = new WineRuntime.SteamProton(1091500);
        var (runner, process, _, _) = Make();

        var act = () => runner.RunAsync(Installation(), _tmp.Combine("nope.exe"), []);

        await act.Should().ThrowAsync<FileNotFoundException>();
        await process.DidNotReceiveWithAnyArgs().RunAsync(default(Command)!, default, default);
    }
}
