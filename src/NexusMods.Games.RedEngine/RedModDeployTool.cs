using System.Reflection;
using CliWrap;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.Generic;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using R3;
using static NexusMods.Games.RedEngine.Constants;

namespace NexusMods.Games.RedEngine;

public class RedModDeployTool : ITool
{
    private readonly GameToolRunner _toolRunner;
    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly ILogger _logger;

    public RedModDeployTool(GameToolRunner toolRunner, TemporaryFileManager temporaryFileManager, ILogger<RedModDeployTool> logger)
    {
        _logger = logger;
        _toolRunner = toolRunner;
        _temporaryFileManager = temporaryFileManager;
    }
    
    public IEnumerable<GameId> GameIds => [Cyberpunk2077.Cyberpunk2077Game.GameId];

    public async Task Execute(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        var deployFolder = loadout.InstallationInstance.Locations.ToAbsolutePath(RedModDeployFolder);

        if (loadout.Installation.Store != GameStore.Steam)
        {
            _logger.LogWarning("Skip running redmod, it's only supported for Steam on Linux at the moment");
            return;
        }
        // The apply wrote the load order (RedModSortOrderVariety as ISortOrderFile) before this tool runs.
        // No file means no prefix, or the file is a symlink the app refuses to write: nothing to deploy with.
        if (!loadout.InstallationInstance.Locations.TryGetValue(RedModListPath.LocationId, out _))
        {
            _logger.LogWarning("Sin prefix de Wine no hay {Path}; no se corre el deploy de REDmod", RedModListPath);
            return;
        }
        var modList = loadout.InstallationInstance.Locations.ToAbsolutePath(RedModListPath);
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
        await using var batchPath = await ExtractTemporaryDeployScript();
        var command = Cli.Wrap(batchPath.ToString()).WithArguments(["deploy", "-force", "-modlist=C:\\modlist.txt"], escape: true);
        await _toolRunner.ExecuteAsync(loadout, command, true, cancellationToken);
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

    private async Task<TemporaryPath> ExtractTemporaryDeployScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "deploy_redmod.bat";

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new InvalidOperationException($"Resource {resourceName} not found in assembly.");

        using var reader = new StreamReader(stream);
        var file = _temporaryFileManager.CreateFile((Extension?)".bat");
        await file.Path.WriteAllTextAsync(await reader.ReadToEndAsync());
        return file;
    }
}
