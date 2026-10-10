using System.Reactive.Disposables;
using Avalonia.Input;
using Avalonia.ReactiveUI;
using R3;
using ReactiveUI;

namespace NexusMods.App.UI.Overlays;

public partial class LocalFileMetadataOverlayView : ReactiveUserControl<ILocalFileMetadataOverlayViewModel>
{
    public LocalFileMetadataOverlayView()
    {
        InitializeComponent();
        this.WhenActivated(disposables =>
        {
            this.BindCommand(ViewModel, vm => vm.CommandCancel, v => v.ButtonCancel).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CommandAccept, v => v.ButtonAccept).DisposeWith(disposables);
            TextBoxName.Focus();
        });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel is { } vm)
        {
            vm.CommandCancel.Execute(Unit.Default);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
