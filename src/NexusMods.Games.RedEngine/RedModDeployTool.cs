using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using R3;
using static NexusMods.Games.RedEngine.Constants;

namespace NexusMods.Games.RedEngine;

/// <summary>
/// Runs <c>redMod.exe deploy</c> in the installation's Wine runtime (<see cref="IWineToolRunner"/>) after the apply
/// wrote <c>modlist.txt</c>. Without a runtime there is nothing to deploy with and the apply goes on; with one, a
/// failed deploy fails the apply on purpose (otherwise the game runs with REDmods that were never deployed).
/// </summary>
public class RedModDeployTool : ITool
{
    private readonly IWineToolRunner _runner;
    private readonly ILogger _logger;

    public RedModDeployTool(IWineToolRunner runner, ILogger<RedModDeployTool> logger)
    {
        _runner = runner;
        _logger = logger;
    }
    
    public IEnumerable<GameId> GameIds => [Cyberpunk2077.Cyberpunk2077Game.GameId];

    public async Task Execute(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        var installation = loadout.InstallationInstance;
        var deployFolder = installation.Locations.ToAbsolutePath(RedModDeployFolder);

        var provider = installation.LocatorResult.LinuxCompatabilityDataProvider;
        if (provider?.Runtime is null)
        {
            _logger.LogWarning("Esta instalación no tiene un entorno de Wine conocido (Steam con protontricks, o un prefix de Lutris); no se corre el deploy de REDmod");
            return;
        }
        // The apply wrote the load order (RedModSortOrderVariety as ISortOrderFile) before this tool runs.
        // No file means no prefix, or the file is a symlink the app refuses to write: nothing to deploy with.
        if (!installation.Locations.TryGetValue(RedModListPath.LocationId, out _))
        {
            _logger.LogWarning("Sin prefix de Wine no hay {Path}; no se corre el deploy de REDmod", RedModListPath);
            return;
        }
        var modList = installation.Locations.ToAbsolutePath(RedModListPath);
        if (!modList.FileExists)
        {
            _logger.LogWarning("No existe {Path}; no se corre el deploy de REDmod", modList);
            return;
        }

        // RedMod deploys to this folder and freaks out if it doesn't exist, but our synchronizer will
        // delete it if it's empty, so we need to recreate it here.
        if (!deployFolder.DirectoryExists())
            deployFolder.CreateDirectory();

        // Note (halgari): When we change the redmod order, we need to force redmod to re-run. This is due to a bug in redmod
        // not recognizing order as a change in the mod configuration. If that bug ever gets fixed (does an order check) then
        // we can resolve this by removing the -force flag.
        var exe = installation.Locations.ToAbsolutePath(RedModPath);
        var modListForWindows = WinePaths.ToWindowsPath(modList, provider.WinePrefixDirectoryPath);
        var result = await _runner.RunAsync(installation, exe, ["deploy", "-force", $"-modlist={modListForWindows}"], cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"El deploy de REDmod terminó con código {result.ExitCode}; revisá Logs/ProcessLogs");
    }

    public string Name => "RedMod Deploy";

    public IJobTask<ITool, Unit> StartJob(Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken cancellationToken)
    {
        return monitor.Begin<ITool, Unit>(this, async _ =>
        {
            await Execute(loadout, cancellationToken);
            return Unit.Default;
        });
    }
}
