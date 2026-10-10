using NexusMods.Abstractions.Library;
using R3;

namespace NexusMods.App.UI.Overlays;

public record struct LocalFileMetadataOverlayResult(bool Confirmed, LocalFileMetadata Metadata)
{
    public static readonly LocalFileMetadataOverlayResult Cancel = new(Confirmed: false, Metadata: LocalFileMetadata.Empty);
}

public interface ILocalFileMetadataOverlayViewModel : IOverlayViewModel<LocalFileMetadataOverlayResult>
{
    string Title { get; }
    string AcceptText { get; }
    /// <summary>Where the file was picked from, or its recorded OriginalPath when editing. Read-only text.</summary>
    string OriginPath { get; }
    BindableReactiveProperty<string> Name { get; }
    BindableReactiveProperty<string> Version { get; }
    BindableReactiveProperty<string> Source { get; }
    BindableReactiveProperty<string> PageUrl { get; }
    IReadOnlyBindableReactiveProperty<bool> IsUrlValid { get; }
    ReactiveCommand<Unit> CommandCancel { get; }
    ReactiveCommand<Unit> CommandAccept { get; }
}
