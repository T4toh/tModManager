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
}
