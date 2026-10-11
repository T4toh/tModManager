using JetBrains.Annotations;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts;

/// <summary>
/// One key the loadout owns inside an intrinsic settings file (see ASettingsIntrinsicFile). A plain
/// LoadoutItem on purpose, not a LoadoutItemWithTargetPath: the winning-files query only looks at
/// items with a TargetPath, so an entry is never "a file" in the sync tree and never collides with
/// the intrinsic node itself. It lives in any group: a mod, a collection, External Changes or the
/// loadout's "Ajustes" group, and follows the group's enabled state like a file would.
/// </summary>
[PublicAPI]
[Include<LoadoutItem>]
public partial class IntrinsicFileEntry : IModelDefinition
{
    private const string Namespace = "NexusMods.Loadouts.IntrinsicFileEntry";

    /// <summary>The intrinsic file this entry belongs to.</summary>
    public static readonly GamePathAttribute File = new(Namespace, nameof(File)) { IsIndexed = true };

    /// <summary>Key as the file's format understands it (CP2077: "group_name/name").</summary>
    public static readonly StringAttribute Key = new(Namespace, nameof(Key));

    /// <summary>Value as a literal of the file's format (CP2077: a JSON literal such as 5.0, true or "Off").</summary>
    public static readonly StringAttribute Value = new(Namespace, nameof(Value));
}
