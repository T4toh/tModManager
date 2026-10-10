using JetBrains.Annotations;
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
}
