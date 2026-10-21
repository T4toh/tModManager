using System.Collections.ObjectModel;
using System.Reactive;
using NexusMods.App.UI.Controls.GameWidget;
using NexusMods.App.UI.Pages.MyGames.WinePrefix;
using NexusMods.App.UI.WorkspaceSystem;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.MyGames;

public interface IMyGamesViewModel : IPageViewModelInterface
{
    public ReactiveCommand<Unit, Unit> OpenRoadmapCommand { get; }

    public ReactiveCommand<Unit, Unit> AddGameManuallyCommand { get; }
    
    public ReactiveCommand<Unit, Unit> RefreshGamesCommand { get; }

    public ReadOnlyObservableCollection<IGameWidgetViewModel> InstalledGames { get; }

    /// <summary>One panel per installation whose game declares prefix requirements.</summary>
    public ReadOnlyObservableCollection<IWinePrefixStatusViewModel> WinePrefixStatuses { get; }
}
