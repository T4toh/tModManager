using NexusMods.Sdk.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// A sort order that the game reads from a file. The variety implements it; the synchronizer writes
/// the file as an intrinsic on every apply (<see cref="SortOrderIntrinsicFile"/>). The loadout is the
/// only writer: the game never rewrites this file, a hand edit is overwritten on the next apply.
/// </summary>
public interface ISortOrderFile
{
    /// <summary>Games this file belongs to (same pattern as <c>ITool.GameIds</c>).</summary>
    IEnumerable<GameId> GameIds { get; }

    /// <summary>Where the file lives. Its location may not be declared (a Wine prefix that does not exist yet).</summary>
    GamePath Path { get; }

    /// <summary>
    /// Full text of the file for this loadout, read from <paramref name="db"/> (the revision being applied).
    /// The game decides separators and line endings. Empty when there is nothing to order.
    /// </summary>
    string Render(Loadout.ReadOnly loadout, IDb db);
}
