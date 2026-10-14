using System.Reactive;
using NexusMods.UI.Sdk;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.MyGames.WinePrefix;

public interface IWinePrefixStatusViewModel : IViewModelInterface
{
    bool IsVisible { get; }
    bool IsExpanded { get; set; }
    bool HasIssues { get; }
    bool IsProtontricksInstalled { get; }
    /// <summary>The game's winetricks packages, in declared order.</summary>
    IReadOnlyList<WinetricksPackageStatus> Packages { get; }
    bool HasCorrectDllOverrides { get; }
    string? DllOverridesInstructions { get; }
    string? WinetricksInstructions { get; }
    string? ProtontricksCommandText { get; }
    bool IsInstallingPackages { get; }
    string? InstallError { get; }
    ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    ReactiveCommand<Unit, Unit> InstallPackagesCommand { get; }
}

public record WinetricksPackageStatus(string Name, bool IsInstalled);
