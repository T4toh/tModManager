using System.Collections.Immutable;
using JetBrains.Annotations;
using NexusMods.Paths;

namespace NexusMods.Sdk.Games;

/// <summary>
/// How an installation runs a Windows executable. Pure data: the tool runner turns it into a command.
/// Null on the provider means the app does not know how (no Lutris config for the prefix, unsupported runner).
/// </summary>
[PublicAPI]
public abstract record WineRuntime
{
    /// <summary>Steam/Proton: protontricks resolves Proton, the Steam Linux Runtime and the prefix from the app id.</summary>
    public sealed record SteamProton(long AppId) : WineRuntime;

    /// <summary>A Wine build (Lutris runner): <c>bin/wine</c> inside <paramref name="RunnerDirectory"/>.</summary>
    public sealed record Wine(AbsolutePath RunnerDirectory, ImmutableDictionary<string, string> Environment) : WineRuntime;

    /// <summary>A Proton build outside Steam (Lutris with GE-Proton): launched through <c>umu-run</c>.</summary>
    public sealed record ProtonViaUmu(AbsolutePath ProtonDirectory, ImmutableDictionary<string, string> Environment) : WineRuntime;
}
