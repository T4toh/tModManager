using System.Text.Json.Nodes;
using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>UserSettings.json as an intrinsic settings file: entries, ingest of game changes, regeneration, reset.</summary>
public class IntrinsicSettingsFileTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<IntrinsicSettingsFileTests>(helper)
{
    protected override bool WithWinePrefix => true;

    private const string SettingsRelative = "drive_c/users/steamuser/AppData/Local/CD Projekt Red/Cyberpunk 2077/UserSettings.json";
    private static readonly GamePath SettingsPath = new(LocationId.WinePrefix, SettingsRelative);
    private const string Dlss = "/graphics/advanced/DLSS";
    private const string Original = """
        {"version":140,"data":[
          {"group_name":"/controls/fpp_camera","options":[{"name":"FPP_MouseX","type":"float","value":5.0,"default_value":5.0}]},
          {"group_name":"/graphics/advanced","options":[{"name":"DLSS","type":"name_list","value":"Auto","default_value":"Auto"}]}
        ]}
        """;

    private AbsolutePath Settings => GameInstallation.Locations[LocationId.WinePrefix].Path.Combine(SettingsRelative);

    private async Task<Loadout.ReadOnly> ManagedWith(string content)
    {
        Settings.Parent.CreateDirectory();
        await Settings.WriteAllTextAsync(content);
        await LoadoutManager.ManageInstallation(GameInstallation);
        await Synchronizer.ReindexState(GameInstallation);
        return await Synchronizer.Synchronize(await CreateLoadout());
    }

    private async Task<LoadoutItemGroupId> ModWithEntry(Loadout.ReadOnly loadout, string name, string key, string value)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        _ = new IntrinsicFileEntry.New(tx, out var id)
        {
            File = SettingsPath, Key = key, Value = value,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = key, LoadoutId = loadout.LoadoutId, ParentId = group },
        };
        var result = await tx.Commit();
        return result[group];
    }

    private async Task<Loadout.ReadOnly> Apply(Loadout.ReadOnly loadout)
    {
        Refresh(ref loadout);
        return await Synchronizer.Synchronize(loadout);
    }

    private async Task<string> ValueOnDisk(string group, string name)
    {
        var doc = JsonNode.Parse(await Settings.ReadAllTextAsync())!;
        var g = doc["data"]!.AsArray().First(x => x!["group_name"]!.GetValue<string>() == group)!;
        return g["options"]!.AsArray().First(o => o!["name"]!.GetValue<string>() == name)!["value"]!.ToJsonString();
    }

    private async Task GameWrites(Func<JsonNode, JsonNode> edit)
    {
        var doc = edit(JsonNode.Parse(await Settings.ReadAllTextAsync())!);
        await Settings.WriteAllTextAsync(doc.ToJsonString());
    }

    private IntrinsicFileEntry.ReadOnly[] ExternalChangeEntries(Loadout.ReadOnly loadout)
    {
        Refresh(ref loadout);
        if (!LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.LoadoutId).TryGetFirst(out var overrides)) return [];
        return overrides.AsLoadoutItemGroup().Children.Select(c => IntrinsicFileEntry.Load(Connection.Db, c.Id)).Where(e => e.IsValid()).ToArray();
    }

    [Fact]
    public void IntrinsicPathEqualsTheWhitelistPath()
    {
        var prefix = GameInstallation.Locations[LocationId.WinePrefix].Path;
        Cyberpunk2077Game.UserSettingsPath(prefix).Should().Be((RelativePath)SettingsRelative);
        GameInstallation.Locations[LocationId.WinePrefix].ManagedFiles.Should().Contain(Cyberpunk2077Game.UserSettingsPath(prefix));
    }

    [Fact]
    public async Task EntryInAMod_IsWrittenAndTheRestIsKept()
    {
        var loadout = await ManagedWith(Original);
        await ModWithEntry(loadout, "DLSS Off", Dlss, "\"Off\"");

        await Apply(loadout);

        (await ValueOnDisk("/graphics/advanced", "DLSS")).Should().Be("\"Off\"");
        (await ValueOnDisk("/controls/fpp_camera", "FPP_MouseX")).Should().Be("5.0");
        JsonNode.Parse(await Settings.ReadAllTextAsync())!["version"]!.GetValue<int>().Should().Be(140);
    }

    [Fact]
    public async Task GameChangesForeignKeys_BaseUpdatesAndOurKeyStaysInTheSameApply()
    {
        var loadout = await ManagedWith(Original);
        await ModWithEntry(loadout, "DLSS Off", Dlss, "\"Off\"");
        loadout = await Apply(loadout);

        await GameWrites(doc =>
        {
            doc["data"]![0]!["options"]![0]!["value"] = 12.5;   // a key nobody owns
            doc["data"]!.AsArray().Add(new JsonObject { ["group_name"] = "/new", ["options"] = new JsonArray(new JsonObject { ["name"] = "X", ["value"] = 1 }) });
            return doc;                                          // DLSS stays "Off": the game did not touch our key
        });

        await Apply(loadout);

        (await ValueOnDisk("/controls/fpp_camera", "FPP_MouseX")).Should().Be("12.5");
        (await ValueOnDisk("/new", "X")).Should().Be("1");
        (await ValueOnDisk("/graphics/advanced", "DLSS")).Should().Be("\"Off\"");
        ExternalChangeEntries(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task GameChangesOurKey_BecomesAnExternalChangeThatWins_AndDeletingItRestoresTheModValue()
    {
        var loadout = await ManagedWith(Original);
        await ModWithEntry(loadout, "DLSS Off", Dlss, "\"Off\"");
        loadout = await Apply(loadout);

        await GameWrites(doc => { doc["data"]![1]!["options"]![0]!["value"] = "Quality"; return doc; });
        loadout = await Apply(loadout);

        var external = ExternalChangeEntries(loadout).Should().ContainSingle().Subject;
        external.Key.Should().Be(Dlss);
        external.Value.Should().Be("\"Quality\"");
        (await ValueOnDisk("/graphics/advanced", "DLSS")).Should().Be("\"Quality\"");

        using (var tx = Connection.BeginTransaction())
        {
            tx.Delete(external.Id, recursive: false);
            await tx.Commit();
        }
        await Apply(loadout);

        (await ValueOnDisk("/graphics/advanced", "DLSS")).Should().Be("\"Off\"");
    }

    [Fact]
    public async Task GameDeletesTheFile_ItIsRegenerated()
    {
        var loadout = await ManagedWith(Original);
        await ModWithEntry(loadout, "DLSS Off", Dlss, "\"Off\"");
        loadout = await Apply(loadout);

        Settings.Delete();
        await Apply(loadout);

        Settings.FileExists.Should().BeTrue();
        (await ValueOnDisk("/graphics/advanced", "DLSS")).Should().Be("\"Off\"");
        (await ValueOnDisk("/controls/fpp_camera", "FPP_MouseX")).Should().Be("5.0", "the base snapshot carried the game's keys");
    }

    [Fact]
    public async Task CorruptFileOnDisk_IsRegeneratedFromTheLastGoodBase()
    {
        var loadout = await ManagedWith(Original);
        await ModWithEntry(loadout, "DLSS Off", Dlss, "\"Off\"");
        loadout = await Apply(loadout);

        await Settings.WriteAllTextAsync("{\"version\":140,\"data\":[{\"group_name\":\"/graphics/adv");   // truncated write
        await Apply(loadout);

        (await ValueOnDisk("/graphics/advanced", "DLSS")).Should().Be("\"Off\"");
        (await ValueOnDisk("/controls/fpp_camera", "FPP_MouseX")).Should().Be("5.0");
        ExternalChangeEntries(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task DisablingTheMod_KeepsTheLastValueUntilTheGameChangesIt()
    {
        var loadout = await ManagedWith(Original);
        var mod = await ModWithEntry(loadout, "DLSS Off", Dlss, "\"Off\"");
        loadout = await Apply(loadout);
        // The game rewrites the file (same values): the base now carries "Off"
        await GameWrites(doc => doc);
        loadout = await Apply(loadout);

        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(mod, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        await Apply(loadout);

        // Documented limitation: the base absorbed the mod's value; nothing owns the key now, so it stays
        (await ValueOnDisk("/graphics/advanced", "DLSS")).Should().Be("\"Off\"");
    }

    [Fact]
    public async Task UnmanageWithCleanup_RestoresTheBackedUpOriginal()
    {
        var loadout = await ManagedWith(Original);
        await ModWithEntry(loadout, "DLSS Off", Dlss, "\"Off\"");
        await Apply(loadout);
        (await ValueOnDisk("/graphics/advanced", "DLSS")).Should().Be("\"Off\"");

        await LoadoutManager.UnManage(GameInstallation);

        (await Settings.ReadAllTextAsync()).Should().Be(Original);
    }

    [Fact]
    public async Task SettingsFileThatIsASymlink_IsNeverWritten()
    {
        var outside = TemporaryFileManager.CreateFolder().Path.Combine("UserSettings.json");
        await outside.WriteAllTextAsync(Original);
        Settings.Parent.CreateDirectory();
        File.CreateSymbolicLink(Settings.ToString(), outside.ToString());
        await LoadoutManager.ManageInstallation(GameInstallation);
        await Synchronizer.ReindexState(GameInstallation);
        var loadout = await Synchronizer.Synchronize(await CreateLoadout());
        await ModWithEntry(loadout, "DLSS Off", Dlss, "\"Off\"");

        // "The game" edits the real file behind the link; the sync must neither follow the link nor refuse
        await outside.WriteAllTextAsync(Original.Replace("\"Auto\"", "\"Quality\"", StringComparison.Ordinal));
        await Apply(loadout);

        (await outside.ReadAllTextAsync()).Should().Contain("\"Quality\"");
        (await outside.ReadAllTextAsync()).Should().NotContain("\"Off\"");
        new FileInfo(Settings.ToString()).LinkTarget.Should().NotBeNull("the link itself is left in place");
    }
}
