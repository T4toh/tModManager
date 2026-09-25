using R3;

namespace NexusMods.App.UI.Overlays;

public interface ILegacyCleanupOverlayViewModel : IOverlayViewModel
{
    /// <summary>Current wizard step, 1..4.</summary>
    BindableReactiveProperty<int> Step { get; }
    BindableReactiveProperty<string> LegacyDownloadsText { get; }
    BindableReactiveProperty<bool> DeleteProtonPrefix { get; }
    BindableReactiveProperty<bool> IsBusy { get; }

    /// <summary>Error or notice from the last step; empty when there is none.</summary>
    BindableReactiveProperty<string> Message { get; }

    /// <summary>True after step 3 failed: the user may continue to the reset without cleaning the game folder.</summary>
    BindableReactiveProperty<bool> CanSkipCleanup { get; }

    /// <summary>True when step 3 was skipped before the Deep Clean ran (the game folder was not cleaned).</summary>
    BindableReactiveProperty<bool> CleanupSkipped { get; }

    ReactiveCommand<Unit> CommandNext { get; }
    ReactiveCommand<Unit> CommandSkipCleanup { get; }
    ReactiveCommand<Unit> CommandQuit { get; }
    ReactiveCommand<Unit> CommandVerifySteam { get; }
}
