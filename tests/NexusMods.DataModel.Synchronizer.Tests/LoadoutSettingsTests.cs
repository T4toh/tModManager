using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.DataModel.Synchronizer;
using NexusMods.Games.TestFramework;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

public class LoadoutSettingsTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<LoadoutSettingsTests>(helper)
{
    protected override bool WithWinePrefix => true;
    private const string Dlss = "/graphics/advanced/DLSS";

    [Fact]
    public async Task TryResolveFile_WithoutArgument_PicksTheOnlyIntrinsic()
    {
        var loadout = await CreateLoadout();
        LoadoutSettings.TryResolveFile(loadout, null, out var path, out var file, out var error).Should().BeTrue(error);
        path.LocationId.Should().Be(LocationId.WinePrefix);
        path.Path.FileName.ToString().Should().Be("UserSettings.json");
        file.Should().BeAssignableTo<ISettingsIntrinsicFile>();
    }

    [Fact]
    public async Task TryResolveFile_WithArgument_ParsesLocationAndPath()
    {
        var loadout = await CreateLoadout();
        LoadoutSettings.TryResolveFile(loadout, null, out var expected, out _, out _).Should().BeTrue();
        LoadoutSettings.TryResolveFile(loadout, $"WinePrefix:{expected.Path}", out var path, out _, out var error).Should().BeTrue(error);
        path.Should().Be(expected);
        LoadoutSettings.TryResolveFile(loadout, "Game:nope.json", out _, out _, out error).Should().BeFalse();
        error.Should().Contain("nope.json");
        error.Should().Contain($"WinePrefix:{expected.Path}", "the options are printed in the form -f accepts");
    }

    [Fact]
    public async Task Upsert_CreatesTheGroupOnce_AndUpdatesTheSameKey()
    {
        var loadout = await CreateLoadout();
        LoadoutSettings.TryResolveFile(loadout, null, out var path, out _, out _).Should().BeTrue();

        var first = await LoadoutSettings.Upsert(Connection, loadout, path, Dlss, "\"Off\"");
        Refresh(ref loadout);
        var second = await LoadoutSettings.Upsert(Connection, loadout, path, Dlss, "\"Quality\"");
        Refresh(ref loadout);

        second.Id.Should().Be(first.Id);
        IntrinsicFileEntry.Load(Connection.Db, first.Id).Value.Should().Be("\"Quality\"");
        var groups = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).Where(i => i.Name == LoadoutSettings.GroupName).ToArray();
        groups.Should().ContainSingle();
    }

    [Fact]
    public async Task List_ShowsOwnerAndWinner()
    {
        var loadout = await CreateLoadout();
        LoadoutSettings.TryResolveFile(loadout, null, out var path, out _, out _).Should().BeTrue();
        await LoadoutSettings.Upsert(Connection, loadout, path, Dlss, "\"Off\"");
        Refresh(ref loadout);

        var rows = LoadoutSettings.List(loadout).ToArray();

        rows.Should().ContainSingle();
        rows[0].Key.Should().Be(Dlss);
        rows[0].Value.Should().Be("\"Off\"");
        rows[0].Group.Should().Be(LoadoutSettings.GroupName);
        rows[0].Wins.Should().BeTrue();
    }

    [Fact]
    public async Task SettingsSet_InvalidValue_IsRejectedBeforeSaving()
    {
        var loadout = await CreateLoadout();
        LoadoutSettings.TryResolveFile(loadout, null, out var path, out var file, out _).Should().BeTrue();

        ((ISettingsIntrinsicFile)file).TryValidate(Dlss, "Off", out var error).Should().BeFalse();
        error.Should().Contain(Dlss);
        IntrinsicFileEntry.FindByFile(Connection.Db, path).Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_DeletesTheEntryFromAjustes_AndReportsWhetherItExisted()
    {
        var loadout = await CreateLoadout();
        LoadoutSettings.TryResolveFile(loadout, null, out var path, out _, out _).Should().BeTrue();
        await LoadoutSettings.Upsert(Connection, loadout, path, Dlss, "\"Off\"");
        Refresh(ref loadout);

        (await LoadoutSettings.Remove(Connection, loadout, path, Dlss, external: false)).Should().BeTrue();
        Refresh(ref loadout);
        IntrinsicFileEntry.FindByFile(Connection.Db, path).Where(e => e.AsLoadoutItem().LoadoutId == loadout.LoadoutId).Should().BeEmpty();
        (await LoadoutSettings.Remove(Connection, loadout, path, Dlss, external: false)).Should().BeFalse("nothing left to remove");
    }

    [Fact]
    public async Task Remove_External_DeletesTheExternalChangeEntry_NotTheHandSetOne()
    {
        var loadout = await CreateLoadout();
        LoadoutSettings.TryResolveFile(loadout, null, out var path, out _, out _).Should().BeTrue();
        await LoadoutSettings.Upsert(Connection, loadout, path, Dlss, "\"Off\"");
        using (var tx = Connection.BeginTransaction())
        {
            Refresh(ref loadout);
            var overrides = LoadoutOverrides.GetOrCreate(tx, loadout);
            _ = new IntrinsicFileEntry.New(tx, out var id)
            {
                File = path, Key = Dlss, Value = "\"Quality\"",
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = Dlss, LoadoutId = loadout.LoadoutId, ParentId = LoadoutItemGroupId.From(overrides.Value) },
            };
            await tx.Commit();
        }
        Refresh(ref loadout);

        (await LoadoutSettings.Remove(Connection, loadout, path, Dlss, external: false)).Should().BeTrue();
        Refresh(ref loadout);
        LoadoutSettings.List(loadout).Should().ContainSingle().Which.Group.Should().Be("Overrides");

        (await LoadoutSettings.Remove(Connection, loadout, path, Dlss, external: true)).Should().BeTrue();
        Refresh(ref loadout);
        LoadoutSettings.List(loadout).Should().BeEmpty();
    }
}
