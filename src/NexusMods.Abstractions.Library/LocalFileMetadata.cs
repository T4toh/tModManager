using JetBrains.Annotations;

namespace NexusMods.Abstractions.Library;

/// <summary>
/// Optional metadata for a file added by hand. Every field may be null or blank; blank values are never stored.
/// </summary>
[PublicAPI]
public record LocalFileMetadata(string? Name = null, string? Version = null, string? Source = null, Uri? PageUri = null)
{
    public static readonly LocalFileMetadata Empty = new();

    public bool IsEmpty => string.IsNullOrWhiteSpace(Name)
                           && string.IsNullOrWhiteSpace(Version)
                           && string.IsNullOrWhiteSpace(Source)
                           && PageUri is null;

    /// <summary>A page is opened in the browser: only http(s) is accepted, from the dialog, the CLI or anywhere else.</summary>
    public static bool IsWebPage(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https";

    /// <summary>Throws when <see cref="PageUri"/> is set and is not an http(s) URL.</summary>
    public void EnsureValidPageUri()
    {
        if (PageUri is { } uri && !IsWebPage(uri))
            throw new ArgumentException($"La página tiene que ser una URL http(s): {uri}");
    }
}
