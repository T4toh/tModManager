using JetBrains.Annotations;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts;

/// <summary>
/// What an intrinsic settings file looks like after the last apply (the disk text, with the loadout's
/// values already in). A deleted or broken file is regenerated from it, and a key only counts as
/// changed by the game when it moved away from this.
/// </summary>
[PublicAPI]
public partial class IntrinsicFileState : IModelDefinition
{
    private const string Namespace = "NexusMods.Loadouts.IntrinsicFileState";

    public static readonly ReferenceAttribute<Loadout> Loadout = new(Namespace, nameof(Loadout)) { IsIndexed = true };
    public static readonly GamePathAttribute File = new(Namespace, nameof(File));
    /// <summary>Text of the file after the last apply.</summary>
    public static readonly StringAttribute BaseContent = new(Namespace, nameof(BaseContent));
}
