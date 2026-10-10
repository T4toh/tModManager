using JetBrains.Annotations;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts;

/// <summary>
/// What an intrinsic settings file looked like on disk the last time it was ingested: everything
/// the loadout does not own. Write renders this base plus the loadout's entries, so a file the game
/// deleted is regenerated whole and Write never has to read the disk.
/// </summary>
[PublicAPI]
public partial class IntrinsicFileState : IModelDefinition
{
    private const string Namespace = "NexusMods.Loadouts.IntrinsicFileState";

    public static readonly ReferenceAttribute<Loadout> Loadout = new(Namespace, nameof(Loadout)) { IsIndexed = true };
    public static readonly GamePathAttribute File = new(Namespace, nameof(File));
    /// <summary>Text of the file as last read from disk.</summary>
    public static readonly StringAttribute BaseContent = new(Namespace, nameof(BaseContent));
    /// <summary>xxHash3 of that text, to skip a re-ingest of identical content.</summary>
    public static readonly HashAttribute IngestedHash = new(Namespace, nameof(IngestedHash));
}
