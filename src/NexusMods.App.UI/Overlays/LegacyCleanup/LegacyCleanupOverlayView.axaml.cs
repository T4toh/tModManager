using Avalonia.ReactiveUI;
using R3;
using ReactiveUI;

namespace NexusMods.App.UI.Overlays;

public partial class LegacyCleanupOverlayView : ReactiveUserControl<ILegacyCleanupOverlayViewModel>
{
    public LegacyCleanupOverlayView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.BindCommand(ViewModel, vm => vm.CommandNext, view => view.ButtonNext)
                .AddTo(disposables);

            this.BindCommand(ViewModel, vm => vm.CommandQuit, view => view.ButtonQuit)
                .AddTo(disposables);

            this.BindCommand(ViewModel, vm => vm.CommandVerifySteam, view => view.ButtonVerifySteam)
                .AddTo(disposables);

            this.Bind(ViewModel, vm => vm.DeleteProtonPrefix.Value, view => view.DeleteProtonPrefixCheckBox.IsChecked)
                .AddTo(disposables);

            this.OneWayBind(ViewModel, vm => vm.LegacyDownloadsText.Value, view => view.LegacyDownloadsTextBlock.Text)
                .AddTo(disposables);

            this.WhenAnyValue(view => view.ViewModel!.Message.Value)
                .Subscribe(message =>
                {
                    MessageTextBlock.Text = message;
                    MessageTextBlock.IsVisible = message.Length > 0;
                })
                .AddTo(disposables);

            this.WhenAnyValue(view => view.ViewModel!.Step.Value)
                .Subscribe(step =>
                {
                    Step1Panel.IsVisible = step == 1;
                    Step2Panel.IsVisible = step == 2;
                    Step3Panel.IsVisible = step == 3;
                    Step4Panel.IsVisible = step == 4;
                    ButtonNext.Text = step == 4 ? "Reiniciar" : "Siguiente";
                })
                .AddTo(disposables);
        });
    }
}
