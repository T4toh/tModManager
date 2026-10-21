using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.MyGames;

public partial class MyGamesView : ReactiveUserControl<IMyGamesViewModel>
{
    public MyGamesView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
            {
                this.WhenAnyValue(view => view.ViewModel!.InstalledGames)
                    .BindToView(this, view => view.DetectedGamesItemsControl.ItemsSource)
                    .DisposeWith(d);

                this.WhenAnyValue(view  => view.ViewModel!.InstalledGames.Count)
                    .Select(installedCount  => installedCount == 0)
                    .Subscribe(isEmpty =>
                        {
                            DetectedGamesEmptyState.IsActive = isEmpty;
                        }
                    )
                    .DisposeWith(d);

                this.WhenAnyValue(view => view.ViewModel!.WinePrefixStatuses)
                    .BindToView(this, view => view.WinePrefixPanels.ItemsSource)
                    .DisposeWith(d);
            }
        );
    }
}
