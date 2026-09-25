using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.StorageManager;

[UsedImplicitly]
public partial class StorageManagerPageView : ReactiveUserControl<IStorageManagerPageViewModel>
{
    public StorageManagerPageView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
        {
            this.OneWayBind(ViewModel, vm => vm.ArchivesSizeText, view => view.ArchivesSizeText.Text)
                .DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.BackedUpFilesCount, view => view.BackedUpFilesCountText.Text,
                    count => count.ToString())
                .DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.DownloadsFolderSizeText, view => view.DownloadsFolderSizeText.Text)
                .DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.CyberpunkBackupsSizeText, view => view.CyberpunkBackupsSizeText.Text)
                .DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.IsBusy, view => view.BusyIndicator.IsVisible)
                .DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.RunGarbageCollectionCommand, view => view.RunGcButton)
                .DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.RefreshCommand, view => view.RefreshButton)
                .DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.DeepCleanCommand, view => view.DeepCleanButton)
                .DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.DeleteDownloadsCommand, view => view.DeleteDownloadsButton)
                .DisposeWith(d);
        });
    }
}
