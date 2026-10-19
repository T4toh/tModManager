using JetBrains.Annotations;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;

namespace NexusMods.Sdk.Library;

/// <summary>
/// Represents a local file in the library.
/// </summary>
[PublicAPI]
[Include<LibraryFile>]
public partial class LocalFile : IModelDefinition
{
    private const string Namespace = "NexusMods.Library.LocalFile";

    /// <summary>
    /// The original path from where the local file originated from.
    /// </summary>
    public static readonly StringAttribute OriginalPath = new(Namespace, nameof(OriginalPath));

    /// <summary>
    /// Version as the user typed it or as parsed from the file name. Free text, optional.
    /// </summary>
    public static readonly StringAttribute Version = new(Namespace, nameof(Version)) { IsOptional = true };

    /// <summary>
    /// Where the file came from ("mod.io", "GitHub", "foro", ...). Free text, optional. Never a Nexus-specific id.
    /// </summary>
    public static readonly StringAttribute Source = new(Namespace, nameof(Source)) { IsOptional = true };

    /// <summary>
    /// Page of the mod, not the direct download link. Optional.
    /// </summary>
    public static readonly UriAttribute PageUri = new(Namespace, nameof(PageUri)) { IsOptional = true };

    /// <summary>
    /// Makes <paramref name="file"/> stop being a local file (a source such as a collection rescan identified it).
    /// The library file and its archive stay; only the attributes of this model go.
    /// </summary>
    public static void RetractFrom(ITransaction tx, LibraryFile.ReadOnly file)
    {
        if (OriginalPath.TryGetValue(file, out var path)) tx.Retract(file.Id, OriginalPath, path);
        if (Version.TryGetValue(file, out var version)) tx.Retract(file.Id, Version, version);
        if (Source.TryGetValue(file, out var source)) tx.Retract(file.Id, Source, source);
        if (PageUri.TryGetValue(file, out var uri)) tx.Retract(file.Id, PageUri, uri);
    }
}
