using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using System.Collections.Immutable;
using NexusMods.Paths;
using Microsoft.Extensions.Logging;

namespace NexusMods.Backend;

public class ManualLinuxCompatabilityDataProvider : ILinuxCompatabilityDataProvider
{
    private readonly AbsolutePath _winePrefix;
    private readonly ILogger _logger;

    public AbsolutePath WinePrefixDirectoryPath => _winePrefix;

    public WineRuntime? Runtime => null;

    public ManualLinuxCompatabilityDataProvider(AbsolutePath winePrefix, ILogger logger)
    {
        _winePrefix = winePrefix;
        _logger = logger;
    }

    public async ValueTask<ImmutableHashSet<string>> GetInstalledWinetricksComponents(CancellationToken cancellationToken = default)
    {
        var winetricksLog = _winePrefix.Combine("winetricks.log");
        if (!winetricksLog.FileExists)
        {
            _logger.LogWarning("Winetricks log not found at {Path}", winetricksLog);
            return ImmutableHashSet<string>.Empty;
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(winetricksLog.ToString(), cancellationToken);
            return lines.Select(l => l.Trim()).ToImmutableHashSet();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Reading winetricks log at {Path} was cancelled", winetricksLog);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read winetricks log at {Path}", winetricksLog);
            return ImmutableHashSet<string>.Empty;
        }
    }

    public async ValueTask<ImmutableArray<WineDllOverride>> GetWineDllOverrides(CancellationToken cancellationToken = default)
    {
        // First, check the wine registry (set via winecfg or regedit)
        var fromRegistry = await GetWineDllOverridesFromRegistry(cancellationToken);
        if (!fromRegistry.IsEmpty) return fromRegistry;

        // Fall back to Lutris game config YAMLs — Lutris sets WINEDLLOVERRIDES as an environment variable,
        // not in the registry, so the registry check above returns nothing for Lutris-managed games.
        var fromLutris = WineParser.ParseDllOverridesFromLutrisConfigs(_winePrefix.ToString());
        return fromLutris;
    }

    private async ValueTask<ImmutableArray<WineDllOverride>> GetWineDllOverridesFromRegistry(CancellationToken cancellationToken)
    {
        var userReg = _winePrefix.Combine("user.reg");
        if (!userReg.FileExists)
        {
            _logger.LogWarning("WINE registry (user.reg) not found at {Path}", userReg);
            return ImmutableArray<WineDllOverride>.Empty;
        }

        try
        {
            var content = await File.ReadAllTextAsync(userReg.ToString(), cancellationToken);
            return WineParser.ParseDllOverridesFromRegistry(content).ToImmutableArray();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Reading WINE registry at {Path} was cancelled", userReg);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse WINE registry at {Path}", userReg);
            return ImmutableArray<WineDllOverride>.Empty;
        }
    }
}
