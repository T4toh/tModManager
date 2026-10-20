using CliWrap;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Games.Generic.Dependencies;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.Generic;

/// <summary>
/// Runs Windows executables in an installation's <see cref="WineRuntime"/>: Steam/Proton through
/// <c>protontricks-launch</c>, a Lutris wine build through its <c>bin/wine</c>, a Lutris Proton build through
/// <c>umu-run</c>. Knows installations, never games: the tool decides what to run.
/// </summary>
public sealed class GameToolRunner : IWineToolRunner
{
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<GameToolRunner> _logger;
    private readonly IProtontricksDependency? _protontricks;

    /// <summary/>
    public GameToolRunner(IProcessRunner processRunner, ILogger<GameToolRunner> logger, IProtontricksDependency? protontricks = null)
    {
        _processRunner = processRunner;
        _logger = logger;
        _protontricks = protontricks;
    }

    /// <inheritdoc />
    public async Task<CommandResult> RunAsync(
        GameInstallation installation,
        AbsolutePath executable,
        IReadOnlyList<string> arguments,
        AbsolutePath? workingDirectory = null,
        bool logOutput = true,
        CancellationToken cancellationToken = default)
    {
        if (!executable.FileExists)
            throw new FileNotFoundException($"No existe el ejecutable {executable}", executable.ToString());
        var workingDir = (workingDirectory ?? executable.Parent).ToString();

        if (!FileSystem.Shared.OS.IsLinux)
        {
            var native = Cli.Wrap(executable.ToString()).WithArguments(arguments, escape: true).WithWorkingDirectory(workingDir).WithValidation(CommandResultValidation.None);
            return await _processRunner.RunAsync(native, logOutput, cancellationToken);
        }

        var provider = installation.LocatorResult.LinuxCompatabilityDataProvider;
        var runtime = provider?.Runtime
            ?? throw new WineRuntimeUnavailableException("Esta instalación no tiene un entorno de Wine conocido (Steam con protontricks, o un prefix configurado en Lutris)");
        var prefix = provider!.WinePrefixDirectoryPath.ToString();

        var command = runtime switch
        {
            WineRuntime.SteamProton steam => await ThroughProtontricks(steam, executable, arguments, workingDir),
            WineRuntime.Wine wine => Cli.Wrap(wine.RunnerDirectory.Combine("bin/wine").ToString())
                .WithArguments([executable.ToString(), ..arguments], escape: true)
                .WithEnvironmentVariables(wine.Environment.SetItem("WINEPREFIX", prefix)!),
            WineRuntime.ProtonViaUmu proton => Cli.Wrap(FindUmuRun().ToString())
                .WithArguments([executable.ToString(), ..arguments], escape: true)
                .WithEnvironmentVariables(proton.Environment
                    .SetItem("WINEPREFIX", prefix)
                    .SetItem("PROTONPATH", proton.ProtonDirectory.ToString())
                    .SetItem("GAMEID", "umu-default")!),
            _ => throw new WineRuntimeUnavailableException($"Entorno de Wine desconocido: {runtime.GetType().Name}"),
        };
        command = command.WithWorkingDirectory(workingDir).WithValidation(CommandResultValidation.None);

        _logger.LogInformation("Corriendo {Exe} en el entorno {Runtime} de {Game}", executable.FileName, runtime.GetType().Name, installation.Game.DisplayName);
        return await _processRunner.RunAsync(command, logOutput, cancellationToken);
    }

    private async Task<Command> ThroughProtontricks(WineRuntime.SteamProton steam, AbsolutePath executable, IReadOnlyList<string> arguments, string workingDir)
    {
        if (_protontricks is null)
            throw new WineRuntimeUnavailableException("protontricks no está instalado; sin él no se puede correr nada en el prefix de Proton de Steam");
        // protontricks >= 1.12 keeps the caller's working directory; older versions used the exe's folder. Both land right.
        var inner = Cli.Wrap(executable.ToString()).WithArguments(arguments, escape: true).WithWorkingDirectory(workingDir).WithValidation(CommandResultValidation.None);
        return await _protontricks.MakeLaunchCommand(inner, steam.AppId);
    }

    /// <summary><c>umu-run</c> from PATH, else the copy Lutris ships (<c>$XDG_DATA_HOME/lutris/runtime/umu/umu-run</c>).</summary>
    private static AbsolutePath FindUmuRun()
    {
        var fs = FileSystem.Shared;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = fs.FromUnsanitizedFullPath(dir).Combine("umu-run");
            if (candidate.FileExists) return candidate;
        }
        var fromLutris = LutrisConfigReader.DefaultDataDirectory(fs).Combine("runtime/umu/umu-run");
        if (fromLutris.FileExists) return fromLutris;
        throw new WineRuntimeUnavailableException("No se encontró umu-run (ni en el PATH ni en el runtime de Lutris); hace falta para un runner de Proton fuera de Steam");
    }
}
