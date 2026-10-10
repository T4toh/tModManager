using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Xunit;

namespace NexusMods.DataModel.Tests;

public class IntrinsicFileModelsTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<IntrinsicFileModelsTests>(helper)
{
    private static readonly GamePath Settings = new(LocationId.WinePrefix, "drive_c/users/steamuser/AppData/Local/CD Projekt Red/Cyberpunk 2077/UserSettings.json");

    [Fact]
    public async Task Entry_RoundTripsAndIsFoundByFile()
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        var entry = new IntrinsicFileEntry.New(tx, out var id)
        {
            File = Settings,
            Key = "/graphics/advanced/DLSS",
            Value = "\"Off\"",
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = "DLSS", LoadoutId = loadout.LoadoutId },
        };
        var result = await tx.Commit();

        var found = IntrinsicFileEntry.FindByFile(Connection.Db, Settings).ToArray();
        found.Should().ContainSingle().Which.Id.Should().Be(result[entry.Id]);
        found[0].Key.Should().Be("/graphics/advanced/DLSS");
        found[0].Value.Should().Be("\"Off\"");
        found[0].AsLoadoutItem().LoadoutId.Should().Be(loadout.LoadoutId);
        // Not a file: the winning-files query must never see it
        LoadoutItemWithTargetPath.Load(Connection.Db, found[0].Id).IsValid().Should().BeFalse();
    }

    [Fact]
    public async Task State_RoundTripsPerLoadout()
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        _ = new IntrinsicFileState.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            File = Settings,
            BaseContent = "{}",
            IngestedHash = Hash.From(1),
        };
        await tx.Commit();

        var state = IntrinsicFileState.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle().Subject;
        state.File.Should().Be(Settings);
        state.BaseContent.Should().Be("{}");
        state.IngestedHash.Should().Be(Hash.From(1));
    }
}
