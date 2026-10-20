using System.Collections.Immutable;
using JetBrains.Annotations;
using NexusMods.Paths;

namespace NexusMods.Sdk.Games;

[PublicAPI]
public interface ILinuxCompatabilityDataProvider
{
    /// <summary>
    /// Path to the WINE prefix directory.
    /// </summary>
    AbsolutePath WinePrefixDirectoryPath { get; }

    /// <summary>
    /// How to run a Windows executable in this prefix; null when unknown (no Lutris config for the prefix,
    /// unsupported runner). Resolved by the locator, like the prefix itself.
    /// </summary>
    WineRuntime? Runtime { get; }

    /// <summary>
    /// Gets all WINE DLL overrides.
    /// </summary>
    ValueTask<ImmutableArray<WineDllOverride>> GetWineDllOverrides(CancellationToken cancellationToken);

    /// <summary>
    /// Gets all installed winetricks components.
    /// </summary>
    ValueTask<ImmutableHashSet<string>> GetInstalledWinetricksComponents(CancellationToken cancellationToken);
}
