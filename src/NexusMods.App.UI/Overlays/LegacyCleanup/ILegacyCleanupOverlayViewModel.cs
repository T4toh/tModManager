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

    ReactiveCommand<Unit> CommandNext { get; }
    ReactiveCommand<Unit> CommandQuit { get; }
    ReactiveCommand<Unit> CommandVerifySteam { get; }
}
