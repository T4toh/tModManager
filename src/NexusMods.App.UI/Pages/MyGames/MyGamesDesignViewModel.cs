using System.Collections.ObjectModel;
using System.Reactive;
using NexusMods.App.UI.Controls.GameWidget;
using NexusMods.App.UI.Pages.MyGames.WinePrefix;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.UI.Sdk;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.MyGames;

public class MyGamesDesignViewModel : APageViewModel<IMyGamesViewModel>, IMyGamesViewModel
{
    public ReactiveCommand<Unit, Unit> OpenRoadmapCommand => Initializers.DisabledReactiveCommand;
    public ReactiveCommand<Unit, Unit> AddGameManuallyCommand => Initializers.DisabledReactiveCommand;
    public ReactiveCommand<Unit, Unit> RefreshGamesCommand => Initializers.DisabledReactiveCommand;
    public ReadOnlyObservableCollection<IGameWidgetViewModel> InstalledGames { get; }
    public ReadOnlyObservableCollection<IWinePrefixStatusViewModel> WinePrefixStatuses { get; } = new([]);

    public MyGamesDesignViewModel() : base(new DesignWindowManager())
    {
        var detectedGames = Enumerable.Range(0, 2)
            .Select(_ => new GameWidgetDesignViewModel())
            .ToArray();

        InstalledGames = new ReadOnlyObservableCollection<IGameWidgetViewModel>(new ObservableCollection<IGameWidgetViewModel>(detectedGames));
    }
}
