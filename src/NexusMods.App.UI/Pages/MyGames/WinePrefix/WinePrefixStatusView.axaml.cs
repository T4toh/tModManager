using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.ReactiveUI;
using NexusMods.UI.Sdk.Icons;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.MyGames.WinePrefix;

public partial class WinePrefixStatusView : ReactiveUserControl<IWinePrefixStatusViewModel>
{
    private static readonly IconValue CheckIcon = IconValues.CheckCircleOutline;
    private static readonly IconValue WarningIcon = IconValues.WarningAmber;

    public WinePrefixStatusView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
        {
            this.WhenAnyValue(v => v.ViewModel!.IsVisible)
                .Subscribe(visible => IsVisible = visible)
                .DisposeWith(d);

            this.WhenAnyValue(v => v.ViewModel!.IsExpanded)
                .BindTo(this, v => v.WinePrefixExpander.IsExpanded)
                .DisposeWith(d);

            // Header icon
            this.WhenAnyValue(v => v.ViewModel!.HasIssues)
                .Subscribe(hasIssues =>
                {
                    StatusIcon.Value = hasIssues ? WarningIcon : CheckIcon;
                })
                .DisposeWith(d);

            // Protontricks status
            this.WhenAnyValue(v => v.ViewModel!.IsProtontricksInstalled)
                .Subscribe(installed =>
                {
                    ProtontricksIcon.Value = installed ? CheckIcon : WarningIcon;
                    ProtontricksText.Text = installed
                        ? "Protontricks: Instalado"
                        : "Protontricks: No encontrado";
                })
                .DisposeWith(d);

            // Winetricks packages status
            this.WhenAnyValue(v => v.ViewModel!.Packages)
                .Subscribe(packages =>
                {
                    PackagesPanel.Children.Clear();
                    foreach (var package in packages)
                    {
                        PackagesPanel.Children.Add(new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Children =
                            {
                                new UnifiedIcon { Size = 16, Value = package.IsInstalled ? CheckIcon : WarningIcon },
                                new TextBlock
                                {
                                    Text = package.IsInstalled ? $"{package.Name}: Instalado" : $"{package.Name}: Faltante",
                                    Theme = this.FindResource("BodyMDNormalTheme") as ControlTheme,
                                    VerticalAlignment = VerticalAlignment.Center,
                                },
                            },
                        });
                    }
                })
                .DisposeWith(d);

            // DLL overrides status
            this.WhenAnyValue(v => v.ViewModel!.HasCorrectDllOverrides)
                .Subscribe(correct =>
                {
                    DllOverridesIcon.Value = correct ? CheckIcon : WarningIcon;
                    DllOverridesText.Text = correct
                        ? "DLL Overrides: Correctos"
                        : "DLL Overrides: Configuracion incorrecta";
                })
                .DisposeWith(d);

            // Instructions visibility
            this.WhenAnyValue(
                    v => v.ViewModel!.DllOverridesInstructions,
                    v => v.ViewModel!.WinetricksInstructions,
                    v => v.ViewModel!.ProtontricksCommandText)
                .Subscribe(tuple =>
                {
                    var (dllInstructions, wtInstructions, command) = tuple;
                    ProtontricksCommandText.Text = command ?? "";
                    ProtontricksCommandPanel.IsVisible = command is not null;
                    DllOverridesInstructionsText.Text = dllInstructions ?? "";
                    DllOverridesInstructionsText.IsVisible = dllInstructions is not null;
                    WinetricksInstructionsText.Text = wtInstructions ?? "";
                    WinetricksInstructionsText.IsVisible = wtInstructions is not null;
                    InstructionsBorder.IsVisible = dllInstructions is not null || wtInstructions is not null;
                })
                .DisposeWith(d);

            this.WhenAnyValue(v => v.ViewModel!.IsInstallingPackages, v => v.ViewModel!.InstallError)
                .Subscribe(tuple =>
                {
                    var (installing, error) = tuple;
                    InstallStatusText.Text = installing ? "Instalando con protontricks… puede tardar unos minutos." : error ?? "";
                    InstallStatusText.IsVisible = installing || error is not null;
                })
                .DisposeWith(d);

            this.BindCommand(ViewModel!, vm => vm.InstallPackagesCommand, v => v.InstallPackagesButton)
                .DisposeWith(d);

            CopyProtontricksCommandButton.Command = ReactiveCommand.CreateFromTask(async () =>
            {
                await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(ViewModel?.ProtontricksCommandText);
            });

            // Refresh button
            this.BindCommand(ViewModel!, vm => vm.RefreshCommand, v => v.RefreshButton)
                .DisposeWith(d);
        });
    }
}
