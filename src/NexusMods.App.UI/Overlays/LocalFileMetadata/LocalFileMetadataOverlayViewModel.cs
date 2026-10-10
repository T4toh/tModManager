using NexusMods.Abstractions.Library;
using NexusMods.Sdk.Library;
using R3;

namespace NexusMods.App.UI.Overlays;

public class LocalFileMetadataOverlayViewModel : AOverlayViewModel<ILocalFileMetadataOverlayViewModel, LocalFileMetadataOverlayResult>, ILocalFileMetadataOverlayViewModel
{
    public string Title { get; }
    public string AcceptText { get; }
    public string OriginPath { get; }
    public BindableReactiveProperty<string> Name { get; }
    public BindableReactiveProperty<string> Version { get; }
    public BindableReactiveProperty<string> Source { get; }
    public BindableReactiveProperty<string> PageUrl { get; }
    public IReadOnlyBindableReactiveProperty<bool> IsUrlValid { get; }
    public ReactiveCommand<Unit> CommandCancel { get; }
    public ReactiveCommand<Unit> CommandAccept { get; }

    public LocalFileMetadataOverlayViewModel(string title, string acceptText, string originPath, LocalFileMetadata initial)
    {
        Title = title;
        AcceptText = acceptText;
        OriginPath = originPath;
        Name = new BindableReactiveProperty<string>(initial.Name ?? string.Empty);
        Version = new BindableReactiveProperty<string>(initial.Version ?? string.Empty);
        Source = new BindableReactiveProperty<string>(initial.Source ?? string.Empty);
        PageUrl = new BindableReactiveProperty<string>(initial.PageUri?.ToString() ?? string.Empty);

        var urlValid = PageUrl.Select(static text => TryParseUrl(text, out _));
        IsUrlValid = urlValid.ToReadOnlyBindableReactiveProperty(initialValue: true);

        CommandCancel = new ReactiveCommand(_ => Complete(result: LocalFileMetadataOverlayResult.Cancel));
        CommandAccept = urlValid.ToReactiveCommand<Unit>(_ =>
        {
            TryParseUrl(PageUrl.Value, out var uri);
            Complete(result: new LocalFileMetadataOverlayResult(Confirmed: true, Metadata: new LocalFileMetadata(Name.Value, Version.Value, Source.Value, uri)));
        }, initialCanExecute: true);
    }

    /// <summary>Empty text is valid (no URL); anything else must be an absolute http(s) URL.</summary>
    private static bool TryParseUrl(string text, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        return Uri.TryCreate(text.Trim(), UriKind.Absolute, out uri) && uri.Scheme is "http" or "https";
    }

    /// <summary>What the "add" dialog starts with: the file name without extension, and the version if the name carries one.</summary>
    public static LocalFileMetadata PrefillFor(string fileName) =>
        new(Name: LocalFileNameParser.DisplayName(fileName), Version: LocalFileNameParser.TryParseVersion(fileName));
}
