using CliWrap;
using JetBrains.Annotations;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// Runs a Windows executable in the Wine/Proton environment of an installation (its <see cref="WineRuntime"/>):
/// Steam through protontricks, a Lutris prefix through its wine build or umu. The tool says what to run; the
/// installation says how.
/// </summary>
/// <remarks>
/// Single implementation (<c>GameToolRunner</c> in <c>NexusMods.Games.Generic</c>); the interface exists because
/// <c>RunGameTool</c> lives here and <c>Games.Generic</c> references this project, not the other way round.
/// </remarks>
[PublicAPI]
public interface IWineToolRunner
{
    /// <summary>
    /// Runs <paramref name="executable"/> with <paramref name="arguments"/> in the installation's runtime and waits for it.
    /// The exit code is not validated: the caller decides what a non-zero result means.
    /// </summary>
    /// <param name="workingDirectory">Defaults to the executable's folder.</param>
    /// <param name="logOutput">Writes stdout/stderr to <c>Logs/ProcessLogs/</c>.</param>
    /// <exception cref="WineRuntimeUnavailableException">The installation has no known runtime, or what it needs (protontricks, umu-run) is missing.</exception>
    /// <exception cref="FileNotFoundException">The executable does not exist.</exception>
    Task<CommandResult> RunAsync(
        GameInstallation installation,
        AbsolutePath executable,
        IReadOnlyList<string> arguments,
        AbsolutePath? workingDirectory = null,
        bool logOutput = true,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The installation has no way to run a Windows executable (no Lutris config for its prefix, protontricks or
/// umu-run not installed). The message says what is missing, in Spanish, for the log.
/// </summary>
[PublicAPI]
public sealed class WineRuntimeUnavailableException(string message) : InvalidOperationException(message);
