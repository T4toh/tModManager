using System.Collections.Immutable;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Sdk.Tests;

public class GameLocationsTests
{
    private static readonly GameLocations Locations = GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty
        .Add(LocationId.Game, FileSystem.Shared.FromUnsanitizedFullPath("/games/Cyberpunk 2077")));

    [Test]
    public async Task ToAbsolutePath_InsideTheGame_Resolves()
    {
        var path = Locations.ToAbsolutePath(new GamePath(LocationId.Game, "r6/scripts/mod.reds"));
        await Assert.That(path.ToString()).IsEqualTo("/games/Cyberpunk 2077/r6/scripts/mod.reds");
    }

    [Test]
    [Arguments("../../.bashrc")]
    [Arguments("r6/../../outside.txt")]
    [Arguments("..\\..\\.bashrc")]
    public async Task ToAbsolutePath_WithParentSegment_Throws(string relative)
    {
        // FOMOD destinations and collection.json paths reach here unchanged
        await Assert.That(() => Locations.ToAbsolutePath(new GamePath(LocationId.Game, relative))).Throws<InvalidOperationException>();
    }
}
