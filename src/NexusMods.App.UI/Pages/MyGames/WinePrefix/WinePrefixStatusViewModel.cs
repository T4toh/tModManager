using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using CliWrap;
using NexusMods.Games.Generic.Dependencies;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.UI.Sdk;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Pages.MyGames.WinePrefix;

public class WinePrefixStatusViewModel : AViewModel<IWinePrefixStatusViewModel>, IWinePrefixStatusViewModel
{
    /// <summary>Only games that declare prefix requirements get the panel.</summary>
    public static bool AppliesTo(IGameData game) => game.WinePrefixRequirements is not null;

    private readonly GameInstallation _installation;
    private readonly WinePrefixRequirements _requirements;
    private readonly ILinuxCompatabilityDataProvider? _linuxCompat;
    private readonly IRuntimeDependency? _protontricks;
    private readonly IProcessRunner _processRunner;
    private Command? _installCommand;

    [Reactive] public bool IsVisible { get; set; }
    [Reactive] public bool IsExpanded { get; set; }
    [Reactive] public bool HasIssues { get; set; }
    [Reactive] public bool IsProtontricksInstalled { get; set; }
    [Reactive] public IReadOnlyList<WinetricksPackageStatus> Packages { get; set; } = [];
    [Reactive] public bool HasCorrectDllOverrides { get; set; }
    [Reactive] public string? DllOverridesInstructions { get; set; }
    [Reactive] public string? WinetricksInstructions { get; set; }
    [Reactive] public string? ProtontricksCommandText { get; set; }
    [Reactive] public bool IsInstallingPackages { get; set; }
    [Reactive] public string? InstallError { get; set; }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> InstallPackagesCommand { get; }

    public WinePrefixStatusViewModel(
        GameInstallation installation,
        IEnumerable<IRuntimeDependency> runtimeDependencies,
        IProcessRunner processRunner)
    {
        _processRunner = processRunner;
        _installation = installation;
        _requirements = installation.Game.WinePrefixRequirements ?? new WinePrefixRequirements([], []);
        _linuxCompat = installation.LocatorResult.LinuxCompatabilityDataProvider;
        _protontricks = runtimeDependencies
            .FirstOrDefault(d => d.DisplayName == "Protontricks");

        RefreshCommand = ReactiveCommand.CreateFromTask(RunChecksAsync);
        InstallPackagesCommand = ReactiveCommand.CreateFromTask(
            InstallPackagesAsync,
            this.WhenAnyValue(vm => vm.IsInstallingPackages, vm => vm.ProtontricksCommandText, static (busy, command) => !busy && command is not null)
        );

        this.WhenActivated(d =>
        {
            if (_linuxCompat is null)
            {
                IsVisible = false;
                return;
            }

            IsVisible = true;

            Observable.StartAsync(RunChecksAsync)
                .Subscribe()
                .DisposeWith(d);
        });
    }

    private async Task RunChecksAsync(CancellationToken ct = default)
    {
        if (_linuxCompat is null) return;

        // Check protontricks
        if (_protontricks is not null)
        {
            var info = await _protontricks.QueryInstallationInformation(ct);
            IsProtontricksInstalled = info.HasValue;
        }
        else
        {
            IsProtontricksInstalled = false;
        }

        // Check winetricks packages
        var installedPackages = await _linuxCompat.GetInstalledWinetricksComponents(cancellationToken: ct);
        Packages = _requirements.WinetricksPackages.Select(p => new WinetricksPackageStatus(p, installedPackages.Contains(p))).ToArray();

        var missingPackages = Packages.Where(p => !p.IsInstalled).Select(p => p.Name).ToArray();
        _installCommand = null;
        if (missingPackages.Length == 0)
        {
            WinetricksInstructions = null;
        }
        else if (IsProtontricksInstalled && _protontricks is IProtontricksDependency protontricks && long.TryParse(_installation.LocatorResult.StoreIdentifier, out var appId))
        {
            // Native or Flatpak, whichever is installed: the copied command has to be the one that works here
            _installCommand = await protontricks.MakeInstallCommand(appId, missingPackages.Order(StringComparer.Ordinal));
            WinetricksInstructions = "Faltan paquetes en el prefix. Con el juego cerrado, instalalos con el botón o con este comando en una terminal:";
        }
        else
        {
            WinetricksInstructions = "Faltan paquetes en el prefix. Se instalan con protontricks, que no está instalado: https://github.com/Matoking/protontricks#installation (después, \"Verificar de nuevo\")";
        }
        ProtontricksCommandText = _installCommand is null ? null : $"{_installCommand.TargetFilePath} {_installCommand.Arguments}";

        // Check DLL overrides
        var existingOverrides = await _linuxCompat.GetWineDllOverrides(cancellationToken: ct);
        var allOverridesCorrect = true;

        foreach (var required in _requirements.DllOverrides)
        {
            var found = existingOverrides.FirstOrDefault(o =>
                o.DllName.Equals(required.DllName, StringComparison.OrdinalIgnoreCase));

            if (found.DllName is null || !found.OverrideTypes.SequenceEqual(required.OverrideTypes))
            {
                allOverridesCorrect = false;
                break;
            }
        }

        HasCorrectDllOverrides = allOverridesCorrect;

        if (!allOverridesCorrect)
        {
            var dllOverridesString = _requirements.DllOverrides
                .Select(x => x.ToString())
                .Aggregate((a, b) => $"{a};{b}");

            DllOverridesInstructions = $"""
* Abrir Steam
* Click derecho en el juego
* Click en "Propiedades..."
* Ir a la seccion "General"
* Actualizar "Opciones de lanzamiento" con:

```
WINEDLLOVERRIDES="{dllOverridesString}" %command%
```
""";
        }
        else
        {
            DllOverridesInstructions = null;
        }

        HasIssues = !IsProtontricksInstalled || Packages.Any(p => !p.IsInstalled) || !HasCorrectDllOverrides;
        IsExpanded = HasIssues;
    }

    private async Task InstallPackagesAsync(CancellationToken ct)
    {
        if (_installCommand is null) return;

        IsInstallingPackages = true;
        InstallError = null;
        try
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var command = _installCommand
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr));

            // logOutput: the full output also goes to ProcessLogs
            var result = await _processRunner.RunAsync(command, logOutput: true, cancellationToken: ct);
            if (result.ExitCode != 0)
                InstallError = $"protontricks terminó con error (código {result.ExitCode}): {LastLines(stderr.Length > 0 ? stderr : stdout)}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            InstallError = $"No se pudo correr protontricks: {ex.Message}";
        }
        finally
        {
            IsInstallingPackages = false;
        }

        await RunChecksAsync(ct);
    }

    private static string LastLines(StringBuilder output, int count = 5)
    {
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("\n", lines.TakeLast(count));
    }
}
