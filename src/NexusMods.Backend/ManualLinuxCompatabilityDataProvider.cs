using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using System.Collections.Immutable;
using NexusMods.Paths;
using Microsoft.Extensions.Logging;

namespace NexusMods.Backend;

public class ManualLinuxCompatabilityDataProvider : ILinuxCompatabilityDataProvider
{
    private readonly AbsolutePath _winePrefix;
    private readonly AbsolutePath _lutrisDataDir;
    private readonly ILogger _logger;
    private readonly Lazy<WineRuntime?> _runtime;

    public AbsolutePath WinePrefixDirectoryPath => _winePrefix;

    /// <summary>Resolved from the Lutris game config for this prefix the first time it is asked for; null when there is none.</summary>
    public WineRuntime? Runtime => _runtime.Value;

    /// <param name="lutrisDataDir">Lutris data dir (<c>$XDG_DATA_HOME/lutris</c>); see <see cref="LutrisConfigReader.DefaultDataDirectory"/>.</param>
    public ManualLinuxCompatabilityDataProvider(AbsolutePath winePrefix, AbsolutePath lutrisDataDir, ILogger logger)
    {
        _winePrefix = winePrefix;
        _lutrisDataDir = lutrisDataDir;
        _logger = logger;
        _runtime = new Lazy<WineRuntime?>(ResolveRuntime);
    }

    private WineRuntime? ResolveRuntime()
    {
        var config = LutrisConfigReader.FindByPrefix(_lutrisDataDir, _winePrefix);
        if (config is null)
        {
            _logger.LogWarning("No hay configuración de Lutris para el prefix {Prefix}; la app no sabe cómo correr ejecutables ahí", _winePrefix);
            return null;
        }
        return LutrisConfigReader.ResolveRuntime(config, _lutrisDataDir, message => _logger.LogWarning("{Message}", message));
    }

    private LutrisGameConfig? LutrisConfig => LutrisConfigReader.FindByPrefix(_lutrisDataDir, _winePrefix);

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
        // The registry (winecfg/regedit; vcrun2022 fills it with *msvcp140 etc.) plus what Lutris passes as
        // WINEDLLOVERRIDES from its YAML. Lutris wins per DLL: its value is the one the game actually runs with.
        var result = (await GetWineDllOverridesFromRegistry(cancellationToken)).ToList();
        foreach (var fromLutris in LutrisConfig?.Overrides ?? [])
        {
            result.RemoveAll(o => string.Equals(o.DllName, fromLutris.DllName, StringComparison.OrdinalIgnoreCase));
            result.Add(fromLutris);
        }
        return [..result];
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
