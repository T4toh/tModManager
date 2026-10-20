using System.Collections.Immutable;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.StandardGameLocators.TestHelpers;

/// <summary>
/// A Wine prefix for tests: just a folder, no DLL overrides, no winetricks packages.
/// </summary>
public sealed class StubbedLinuxCompatabilityDataProvider(AbsolutePath winePrefix) : ILinuxCompatabilityDataProvider
{
    public AbsolutePath WinePrefixDirectoryPath => winePrefix;

    /// <summary>Null by default; a test sets it to give the stub installation a way to run executables.</summary>
    public WineRuntime? Runtime { get; set; }

    public ValueTask<ImmutableArray<WineDllOverride>> GetWineDllOverrides(CancellationToken cancellationToken)
        => ValueTask.FromResult(ImmutableArray<WineDllOverride>.Empty);

    public ValueTask<ImmutableHashSet<string>> GetInstalledWinetricksComponents(CancellationToken cancellationToken)
        => ValueTask.FromResult(ImmutableHashSet<string>.Empty);
}
