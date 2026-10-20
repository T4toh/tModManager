using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Exceptions;
using NexusMods.App.UI.Overlays;
using NexusMods.App.UI.Overlays.Generic.MessageBox.Ok;
using NexusMods.App.UI.Resources;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.LeftMenu.Items;

public class LaunchButtonViewModel : AViewModel<ILaunchButtonViewModel>, ILaunchButtonViewModel
{
    [Reactive] public LoadoutId LoadoutId { get; set; } = Initializers.LoadoutId;

    [Reactive] public ReactiveCommand<Unit, Unit> Command { get; set; }

    public IObservable<bool> IsRunningObservable => _gameRunningTracker.GetWithCurrentStateAsStarting();

    [Reactive] public string Label { get; set; } = Language.LaunchButtonViewModel_LaunchGame_LAUNCH;

    [Reactive] public Percent? Progress { get; set; }

    private readonly ILogger<ILaunchButtonViewModel> _logger;
    private readonly IToolManager _toolManager;
    private readonly IConnection _conn;
    private readonly IJobMonitor _monitor;
    private readonly IServiceProvider _serviceProvider;
    private readonly GameRunningTracker _gameRunningTracker;

    public LaunchButtonViewModel(ILogger<ILaunchButtonViewModel> logger, IToolManager toolManager, IConnection conn, IJobMonitor monitor, IServiceProvider serviceProvider, GameRunningTracker gameRunningTracker)
    {
        _logger = logger;
        _toolManager = toolManager;
        _conn = conn;
        _monitor = monitor;
        _serviceProvider = serviceProvider;
        _gameRunningTracker = gameRunningTracker;
        
        this.WhenActivated(cd =>
        {
            _gameRunningTracker.GetWithCurrentStateAsStarting().Subscribe(isRunning =>
            {
                if (isRunning)
                    SetLabelToRunning();
                else
                    SetLabelToLaunch();
            }).DisposeWith(cd);
        });

        var canExecute = this.WhenAnyValue(vm => vm.LoadoutId)
            .Select(id =>
            {
                if (id == Initializers.LoadoutId) return false;
                var loadout = Sdk.Loadouts.Loadout.Load(_conn.Db, id);
                var installation = loadout.InstallationInstance;
                var primaryFile = installation.Locations.ToAbsolutePath(installation.GetGame().GetPrimaryFile(installation));
                return primaryFile.FileExists;
            });

        Command = ReactiveCommand.CreateFromObservable(
            () => Observable.StartAsync(LaunchGame, RxApp.TaskpoolScheduler),
            canExecute);
    }

    private async Task LaunchGame(CancellationToken token)
    {
        var marker = Sdk.Loadouts.Loadout.Load(_conn.Db, LoadoutId);
        SetLabelToRunning();
        try
        {
            var tool = _toolManager.GetTools(marker).OfType<IRunGameTool>().First();
            await Task.Run(async () =>
            {
                await _toolManager.RunTool(tool, marker, _monitor, token: token);
            }, token);
        }
        catch (ExecutableInUseException)
        {
            await MessageBoxOkViewModel.ShowGameAlreadyRunningError(_serviceProvider, marker.Installation.Name);
        }
        catch (WineRuntimeUnavailableException ex)
        {
            // A manually added game without a known Wine runtime: say so instead of a Play that does nothing
            await MessageBoxOkViewModel.Show(_serviceProvider, "No se puede lanzar el juego", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error launching game");
        }
        SetLabelToLaunch();
    }
    private void SetLabelToRunning() => Label = Language.LaunchButtonViewModel_LaunchGame_RUNNING;
    private void SetLabelToLaunch() => Label = Language.LaunchButtonViewModel_LaunchGame_LAUNCH;
}
