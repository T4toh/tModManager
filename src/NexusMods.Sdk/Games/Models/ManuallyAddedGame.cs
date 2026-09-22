using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Sdk.Games;

/// <summary>
/// Used to store information about manually added games.
/// Upstream marked this obsolete; this fork relies on it for the manual game locator.
/// </summary>
public partial class ManuallyAddedGame : IModelDefinition
{
    private const string Namespace = "NexusMods.StandardGameLocators.ManuallyAddedGame";

    // TODO: replace nexus mods game id with game id
    /// <summary>
    /// The game domain this game install belongs to.
    /// </summary>
    public static readonly NexusModsGameIdAttribute GameId = new(Namespace, nameof(GameId)) { IsIndexed = true };

    /// <summary>
    /// The version of the game.
    /// </summary>
    public static readonly StringAttribute Version = new(Namespace, nameof(Version));

    /// <summary>
    /// The path to the game install.
    /// </summary>
    public static readonly StringAttribute Path = new(Namespace, nameof(Path)) { IsIndexed = true };

    /// <summary>
    /// The path to the WINE prefix.
    /// </summary>
    public static readonly StringAttribute WinePrefix = new(Namespace, nameof(WinePrefix));
}
