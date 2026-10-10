using System.Text;
using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>Exercises the base class with a trivial "key=value" line format; no disk, no game format.</summary>
public class SettingsIntrinsicFileTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<SettingsIntrinsicFileTests>(helper)
{
    private static readonly GamePath FilePath = new(LocationId.Game, "settings.txt");

    private sealed class LinesFile() : ASettingsIntrinsicFile<Dictionary<string, string>>(FilePath)
    {
        protected override Dictionary<string, string> Parse(string text) =>
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        protected override string Serialize(Dictionary<string, string> doc) => string.Join('\n', doc.Select(kv => $"{kv.Key}={kv.Value}")) + "\n";
        protected override bool TryGet(Dictionary<string, string> doc, string key, out string value) => doc.TryGetValue(key, out value!);
        protected override void Set(Dictionary<string, string> doc, string key, string value)
        {
            if (value.Contains('\n')) throw new InvalidOperationException($"Valor inválido para '{key}'");
            doc[key] = value;
        }
    }

    private async Task<LoadoutItemGroupId> Mod(Loadout.ReadOnly loadout, string name, params (string Key, string Value)[] entries)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        foreach (var (key, value) in entries)
            _ = new IntrinsicFileEntry.New(tx, out var id)
            {
                File = FilePath, Key = key, Value = value,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = key, LoadoutId = loadout.LoadoutId, ParentId = group },
            };
        var result = await tx.Commit();
        return result[group];
    }

    private static async Task<string> Render(LinesFile file, Loadout.ReadOnly loadout)
    {
        using var ms = new MemoryStream();
        await file.Write(ms, loadout, new Dictionary<GamePath, SyncNode>());
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task<(string? Rewritten, Loadout.ReadOnly Loadout)> IngestText(LinesFile file, Loadout.ReadOnly loadout, string disk)
    {
        using var tx = Connection.BeginTransaction();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(disk));
        var bytes = await file.Ingest(ms, loadout, new Dictionary<GamePath, SyncNode>(), tx);
        await tx.Commit();
        Refresh(ref loadout);
        return (bytes is null ? null : Encoding.UTF8.GetString(bytes.Value.Span), loadout);
    }

    [Fact]
    public async Task Write_WithoutBase_WritesOnlyTheEntries()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", ("dlss", "off"));
        Refresh(ref loadout);

        (await Render(new LinesFile(), loadout)).Should().Be("dlss=off\n");
    }

    [Fact]
    public async Task Ingest_SnapshotsTheBase_AndWriteKeepsIt()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", ("dlss", "off"));
        Refresh(ref loadout);
        var file = new LinesFile();

        var (rewritten, after) = await IngestText(file, loadout, "fov=90\ndlss=auto\n");

        rewritten.Should().Be("fov=90\ndlss=off\n", "our key is re-applied over the game's value in the same apply");
        (await Render(file, after)).Should().Be("fov=90\ndlss=off\n");
        IntrinsicFileState.FindByLoadout(Connection.Db, after.LoadoutId).Should().ContainSingle().Which.BaseContent.Should().Be("fov=90\ndlss=auto\n");
    }

    [Fact]
    public async Task Ingest_GameChangedOurKey_BecomesAnExternalChangeThatWins()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", ("dlss", "off"));
        Refresh(ref loadout);
        var file = new LinesFile();
        (_, loadout) = await IngestText(file, loadout, "dlss=off\n");   // first sight: disk matches, no override

        var (rewritten, after) = await IngestText(file, loadout, "dlss=quality\n");

        rewritten.Should().BeNull("the game's value wins, so the disk already matches");
        var overrides = LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, after.LoadoutId).Should().ContainSingle().Subject;
        var external = overrides.AsLoadoutItemGroup().Children.Select(c => IntrinsicFileEntry.Load(Connection.Db, c.Id)).Where(e => e.IsValid()).ToArray();
        external.Should().ContainSingle().Which.Value.Should().Be("quality");
        (await Render(file, after)).Should().Be("dlss=quality\n");

        // Ingesting the same disk again creates nothing new
        (_, after) = await IngestText(file, after, "dlss=quality\n");
        LoadoutOverridesGroup.Load(Connection.Db, overrides.Id).AsLoadoutItemGroup().Children.Count.Should().Be(1);
    }

    [Fact]
    public async Task Ingest_ForeignKeysOnly_CreatesNoExternalChange()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "A", ("dlss", "off"));
        Refresh(ref loadout);
        var file = new LinesFile();

        var (rewritten, after) = await IngestText(file, loadout, "fov=100\ndlss=off\n");

        rewritten.Should().BeNull();
        IntrinsicFileEntry.FindByFile(Connection.Db, FilePath).Count(e => e.AsLoadoutItem().LoadoutId == after.LoadoutId).Should().Be(1, "only the mod's entry; no External Change");
    }

    [Fact]
    public async Task TwoEntriesForOneKey_NewestWins_AndFallsBackWhenDisabled()
    {
        var loadout = await CreateLoadout();
        await Mod(loadout, "Old", ("dlss", "off"));
        var newer = await Mod(loadout, "New", ("dlss", "quality"));
        Refresh(ref loadout);
        var file = new LinesFile();
        (await Render(file, loadout)).Should().Be("dlss=quality\n");

        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(newer, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        Refresh(ref loadout);

        (await Render(file, loadout)).Should().Be("dlss=off\n");
    }

    [Fact]
    public async Task DisabledMod_IsNotApplied()
    {
        var loadout = await CreateLoadout();
        var group = await Mod(loadout, "A", ("dlss", "off"));
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(group, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        Refresh(ref loadout);

        (await Render(new LinesFile(), loadout)).Should().NotContain("dlss", "a disabled mod owns nothing");
    }

    [Fact]
    public void TryValidate_UsesSet()
    {
        var file = new LinesFile();
        file.TryValidate("a", "1", out var error).Should().BeTrue();
        error.Should().BeNull();
        file.TryValidate("a", "1\n2", out error).Should().BeFalse();
        error.Should().Contain("Valor inválido");
    }
}
