using System.Collections.Immutable;
using JetBrains.Annotations;

namespace NexusMods.Sdk.Games;

/// <summary>
/// What a game's Wine/Proton prefix needs for modding: DLL overrides (Steam launch options or Lutris env)
/// and winetricks packages (installed with protontricks).
/// </summary>
[PublicAPI]
public sealed record WinePrefixRequirements(
    ImmutableArray<WineDllOverride> DllOverrides,
    ImmutableArray<string> WinetricksPackages)
{
    // default(ImmutableArray) throws on Length and enumeration: read it as empty
    public ImmutableArray<WineDllOverride> DllOverrides { get; init; } = DllOverrides.IsDefault ? [] : DllOverrides;
    public ImmutableArray<string> WinetricksPackages { get; init; } = WinetricksPackages.IsDefault ? [] : WinetricksPackages;
}
