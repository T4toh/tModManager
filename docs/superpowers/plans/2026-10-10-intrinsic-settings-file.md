# Intrinsic Settings File (pieza 4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a loadout own individual keys of a settings file the game rewrites (`UserSettings.json` for CP2077), with a base snapshot taken from disk, per-mod entries, and game-made changes to owned keys surfacing as External Changes.

**Architecture:** Two new loadout models (`IntrinsicFileEntry`: a plain `LoadoutItem` with `File`/`Key`/`Value`; `IntrinsicFileState`: per-loadout base snapshot of an intrinsic file). An abstract `ASettingsIntrinsicFile<TDoc>` in the core implements `IIntrinsicFile.Write` (base + winning entries) and `Ingest` (snapshot the disk, turn game changes to owned keys into External Change entries, return the bytes to rewrite). `IIntrinsicFile.Ingest` now returns `Task<ReadOnlyMemory<byte>?>` and `ALoadoutSynchronizer.AdaptLoadout` writes those bytes in the same apply, through the same path guards as `WriteIntrinsic`. CP2077 contributes the only format (`UserSettingsFile`, JSON by `group_name/name`) and declares the file when the Wine prefix location exists. A CLI verb pair (`loadout settings set|list`) feeds entries for real-game testing.

**Tech Stack:** C#/.NET 10, MnemonicDB 0.28.2 (`GamePathAttribute`, `StringAttribute`, `HashAttribute`, `ReferenceAttribute`), `System.Text.Json.Nodes.JsonNode`, NexusMods.Paths, `NexusMods.Hashing.xxHash3` (`byte[].xxHash3()`), xUnit v3 (`dotnet test`) for `NexusMods.DataModel.Synchronizer.Tests`, `NexusMods.DataModel.Tests` and `NexusMods.Games.RedEngine.Tests`.

**Spec:** `docs/superpowers/specs/2026-10-10-intrinsic-settings-file-design.md`

## Global Constraints

- Branch `feat/intrinsic-settings-file` (already created from `main`; the spec is its first commit).
- dotnet lives in `~/.dotnet`; export `DOTNET_ROLL_FORWARD=Major` before any `dotnet` command.
- Build gate: `dotnet build -p:TreatWarningsAsErrors=true` must stay at 0 compiler warnings (only `NU19xx` NuGet audit warnings are tolerated). `.globalconfig` analyzer errors (`CS4014`, `CS8509`, `CA1069`, `CA2211`, `CA2021`) are never suppressed.
- Never run `dotnet test` on the whole solution. One project at a time. TUnit projects run with `dotnet run --project`, not `dotnet test`.
- Nothing CP2077-specific outside `src/NexusMods.Games.RedEngine`; nothing in the core assumes Nexus Mods.
- `ActionMapping.cs` is not touched: `tests/NexusMods.DataModel.Tests/SynchronizerRuleTests.RulesAreAsExpected.verified.txt` must not change (spec criterion 8). No `.verified.` file is edited or deleted by hand; if one changes, stop and report.
- Every write to disk added here goes through `EnsureDiskChangesStayInside` (whitelist, `..`, symlinks). The symlink test must fail without the guard.
- Never `AbsolutePath.DeleteDirectory(recursive: true)`.
- Commit messages: Conventional Commits, no AI co-author line, no "Generated with" footer (org rule).
- Log, exception and CLI strings in Spanish, as the surrounding fork code does. Code comments in English.
- No new NuGet packages (`JsonNode` is in the BCL).

## Review Focus

Inputs the spec implies but did not list as tests. Each has a test pinned to the task that owns the code:

1. The file on disk is not valid JSON (the game crashed mid-write, or the user hand-edited it): `Ingest` must not fail the whole sync; it keeps the previous base, creates no External Changes, and returns the render from the previous base so the file is repaired. Test in Task 4 (`CorruptFileOnDisk_IsRegeneratedFromTheLastGoodBase`).
2. Two enabled mods own the same key: the winner is deterministic (External Changes first, then the newest entity), and disabling the winner falls back to the other. Test in Task 2 (`TwoEntriesForOneKey_NewestWins_AndFallsBackWhenDisabled`).
3. A mod with an entry is disabled after its value was written and the game has since rewritten the file: the key keeps the last value (the base absorbed it) until the game or the user changes it. This is the documented limitation of "base = whole disk"; pin it so it is a decision, not an accident. Test in Task 4 (`DisablingTheMod_KeepsTheLastValueUntilTheGameChangesIt`).
4. The entry's value is not a JSON literal (`abc` instead of `"abc"`): `Set` throws naming the key, the CLI refuses before saving, and a bad entry that got in by other means fails the apply with a clear message instead of writing a corrupt file. Tests in Task 3 (`Set_InvalidLiteral_Throws`) and Task 5 (`SettingsSet_InvalidValue_IsRejectedBeforeSaving`).
5. A Lutris prefix whose user folder is the real user name, not `steamuser`: the intrinsic path must match the whitelist path or nothing is ever written. Pinned by sharing one helper (`Cyberpunk2077Game.UserSettingsPath`) between `GetManagedFiles` and `IntrinsicFiles`; test in Task 4 (`IntrinsicPathEqualsTheWhitelistPath`).

---

### Task 1: Models and the `Ingest` return type

**Files:**
- Create: `src/NexusMods.Abstractions.Loadouts/Models/IntrinsicFileEntry.cs`
- Create: `src/NexusMods.Abstractions.Loadouts/Models/IntrinsicFileState.cs`
- Modify: `src/NexusMods.Abstractions.Loadouts/Services.cs:23-36` (register both models)
- Modify: `src/NexusMods.Abstractions.Loadouts.Synchronizers/IIntrinsicFile.cs`
- Modify: `src/NexusMods.Abstractions.Loadouts.Synchronizers/ALoadoutSynchronizer.cs:618-631` (`AdaptLoadout` discards the result for now)
- Test: `tests/NexusMods.DataModel.Tests/IntrinsicFileModelsTests.cs`

**Interfaces:**
- Produces: `IntrinsicFileEntry` (`[Include<LoadoutItem>]`; `GamePathAttribute File` indexed, `StringAttribute Key`, `StringAttribute Value`; generated `IntrinsicFileEntry.FindByFile(IDb, GamePath)`), `IntrinsicFileState` (`ReferenceAttribute<Loadout> Loadout` indexed, `GamePathAttribute File`, `StringAttribute BaseContent`, `HashAttribute IngestedHash`; generated `IntrinsicFileState.FindByLoadout(IDb, LoadoutId)`), `Task<ReadOnlyMemory<byte>?> IIntrinsicFile.Ingest(Stream, Loadout.ReadOnly, Dictionary<GamePath, SyncNode>, ITransaction)`.

- [ ] **Step 1: Write the failing test**

`tests/NexusMods.DataModel.Tests/IntrinsicFileModelsTests.cs`:

```csharp
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
        found[0].AsLoadoutItem().TryGetAsLoadoutItemWithTargetPath(out _).Should().BeFalse();
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
```

If `TryGetAsLoadoutItemWithTargetPath` is not generated on `LoadoutItem.ReadOnly`, replace that line with `LoadoutItemWithTargetPath.Load(Connection.Db, found[0].Id).IsValid().Should().BeFalse();`.

- [ ] **Step 2: Run to verify it fails**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Tests --filter "FullyQualifiedName~IntrinsicFileModelsTests" 2>&1 | grep -E "error CS|Passed!|Failed!" | head -3`
Expected: build error, `IntrinsicFileEntry` does not exist.

- [ ] **Step 3: Create the models**

`src/NexusMods.Abstractions.Loadouts/Models/IntrinsicFileEntry.cs`:

```csharp
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
```

`src/NexusMods.Abstractions.Loadouts/Models/IntrinsicFileState.cs`:

```csharp
using JetBrains.Annotations;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.Sdk.Games;
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
```

`HashAttribute` lives in `NexusMods.Sdk.Hashes` (see `LibraryFile.cs`); add that `using` if the compiler asks. `GamePathAttribute` is `NexusMods.Sdk.Games.GamePathAttribute` (`src/NexusMods.Sdk/Games/GamePath.cs:154`).

`src/NexusMods.Abstractions.Loadouts/Services.cs`: after `.AddGameBackedUpFileModel()` add

```csharp
            .AddIntrinsicFileEntryModel()
            .AddIntrinsicFileStateModel()
```

- [ ] **Step 4: Change the `Ingest` return type**

`src/NexusMods.Abstractions.Loadouts.Synchronizers/IIntrinsicFile.cs`: replace the `Ingest` member with

```csharp
    /// <summary>
    /// Ingest the contents of the stream into the loadout. Returns the bytes that must end up on disk
    /// afterwards (base plus the loadout's entries, including any External Change created here), or
    /// null when the disk already matches. The synchronizer writes them in the same apply.
    /// </summary>
    public Task<ReadOnlyMemory<byte>?> Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx);
```

`ALoadoutSynchronizer.AdaptLoadout` (line ~629): change `await instance.Ingest(stream, loadout, syncTree, tx);` to `_ = await instance.Ingest(stream, loadout, syncTree, tx);` (Task 4 uses the result).

- [ ] **Step 5: Run to verify it passes, and the rule snapshot is untouched**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Tests --filter "FullyQualifiedName~IntrinsicFileModelsTests|FullyQualifiedName~SynchronizerRuleTests" 2>&1 | grep -E "error CS|Passed!|Failed!" | head -3; git status --short tests | grep verified`
Expected: `Passed!` with 3 tests (2 new + rules); no `.verified` or `.received` file listed.

- [ ] **Step 6: Commit**

```bash
git add src/NexusMods.Abstractions.Loadouts src/NexusMods.Abstractions.Loadouts.Synchronizers/IIntrinsicFile.cs src/NexusMods.Abstractions.Loadouts.Synchronizers/ALoadoutSynchronizer.cs tests/NexusMods.DataModel.Tests/IntrinsicFileModelsTests.cs
git commit -m "feat(loadouts): IntrinsicFileEntry and IntrinsicFileState models; Ingest returns the bytes to rewrite"
```

---

### Task 2: `ASettingsIntrinsicFile<TDoc>` (core)

**Files:**
- Create: `src/NexusMods.Abstractions.Loadouts.Synchronizers/LoadoutOverrides.cs`
- Create: `src/NexusMods.Abstractions.Loadouts.Synchronizers/ASettingsIntrinsicFile.cs`
- Modify: `src/NexusMods.Abstractions.Loadouts.Synchronizers/ALoadoutSynchronizer.cs:183-203` (`GetOrCreateOverridesGroup` delegates to `LoadoutOverrides.GetOrCreate`)
- Test: `tests/NexusMods.DataModel.Synchronizer.Tests/SettingsIntrinsicFileTests.cs`

**Interfaces:**
- Consumes: `IntrinsicFileEntry`, `IntrinsicFileState`, new `Ingest` signature (Task 1).
- Produces: `public static LoadoutOverridesGroupId LoadoutOverrides.GetOrCreate(ITransaction tx, Loadout.ReadOnly loadout)`; `public interface ISettingsIntrinsicFile : IIntrinsicFile { bool TryValidate(string key, string value, out string? error); IReadOnlyDictionary<string, IntrinsicFileEntry.ReadOnly> WinningEntries(Loadout.ReadOnly loadout); }`; `public abstract class ASettingsIntrinsicFile<TDoc>(GamePath path) : ISettingsIntrinsicFile` with `protected abstract TDoc Parse(string text)`, `protected abstract string Serialize(TDoc document)`, `protected abstract bool TryGet(TDoc document, string key, out string value)`, `protected abstract void Set(TDoc document, string key, string value)`, and the two interface members public.

- [ ] **Step 1: Write the failing tests**

The tests use a throwaway `key=value` line format so the base class is exercised without any game format. `tests/NexusMods.DataModel.Synchronizer.Tests/SettingsIntrinsicFileTests.cs`:

```csharp
using System.Text;
using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
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
        overrides.AsLoadoutItemGroup().Rebase().Children.Count.Should().Be(1);
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
        LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, after.LoadoutId).Should().BeEmpty();
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
```

Notes: `AddEmptyGroup(tx, loadoutId, name)` is on `AIsolatedGameTest` (used by `AddModAsync`); if its visibility is `private`, make it `protected`. `Null.Instance` is `NexusMods.MnemonicDB.Abstractions.ElementComparers.Null`, the value used for `MarkerAttribute` (grep `LoadoutItem.Disabled, Null.Instance` for the exact using). `DisabledMod_IsNotApplied` only checks that nothing owned is rendered (an empty base with no entries serializes to `"\n"` in the test format).

- [ ] **Step 2: Run to verify they fail**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Synchronizer.Tests --filter "FullyQualifiedName~SettingsIntrinsicFileTests" 2>&1 | grep -E "error CS|Passed!|Failed!" | head -3`
Expected: build error, `ASettingsIntrinsicFile` does not exist.

- [ ] **Step 3: Extract the overrides-group helper**

`src/NexusMods.Abstractions.Loadouts.Synchronizers/LoadoutOverrides.cs`:

```csharp
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

/// <summary>The loadout's External Changes group ("Overrides"): files and settings the game changed on its own.</summary>
public static class LoadoutOverrides
{
    public static LoadoutOverridesGroupId GetOrCreate(ITransaction tx, Loadout.ReadOnly loadout)
    {
        if (LoadoutOverridesGroup.FindByOverridesFor(loadout.Db, loadout.Id).TryGetFirst(out var found))
            return found;

        var newOverrides = new LoadoutOverridesGroup.New(tx, out var id)
        {
            OverridesForId = loadout,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    Name = "Overrides",
                    LoadoutId = loadout.Id,
                },
            },
        };
        return newOverrides.Id;
    }
}
```

`ALoadoutSynchronizer.GetOrCreateOverridesGroup` body becomes `=> LoadoutOverrides.GetOrCreate(tx, loadout);` (keep the method; callers stay).

- [ ] **Step 4: Write the base class**

`src/NexusMods.Abstractions.Loadouts.Synchronizers/ASettingsIntrinsicFile.cs`:

```csharp
using System.Text;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

/// <summary>An intrinsic file whose entries the CLI can validate before saving.</summary>
public interface ISettingsIntrinsicFile : IIntrinsicFile
{
    bool TryValidate(string key, string value, out string? error);
    /// <summary>Enabled entries for this file, one per key: External Changes win, otherwise the newest entity.</summary>
    IReadOnlyDictionary<string, IntrinsicFileEntry.ReadOnly> WinningEntries(Loadout.ReadOnly loadout);
}

/// <summary>
/// A settings file the game rewrites, where the loadout owns some keys. Write renders the last
/// ingested base plus the winning entries; Ingest snapshots the disk as the new base, turns a
/// game-made change to an owned key into an External Change entry (which wins), and returns the
/// bytes that must be on disk afterwards. The game defines only the format.
/// </summary>
public abstract class ASettingsIntrinsicFile<TDoc>(GamePath path) : ISettingsIntrinsicFile
{
    public GamePath Path { get; } = path;

    /// <summary>Text to document. Empty text must yield a valid empty document.</summary>
    protected abstract TDoc Parse(string text);
    protected abstract string Serialize(TDoc document);
    protected abstract bool TryGet(TDoc document, string key, out string value);
    /// <summary>Throws InvalidOperationException naming the key when the value is not valid for this format.</summary>
    protected abstract void Set(TDoc document, string key, string value);

    public bool TryValidate(string key, string value, out string? error)
    {
        try
        {
            Set(Parse(string.Empty), key, value);
            error = null;
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Enabled entries for this file, one per key. External Changes win; otherwise the newest entity.
    /// </summary>
    public IReadOnlyDictionary<string, IntrinsicFileEntry.ReadOnly> WinningEntries(Loadout.ReadOnly loadout)
    {
        var db = loadout.Db;
        var winners = new Dictionary<string, IntrinsicFileEntry.ReadOnly>();
        var winnerIsOverride = new HashSet<string>();
        foreach (var entry in IntrinsicFileEntry.FindByFile(db, Path))
        {
            var item = entry.AsLoadoutItem();
            if (item.LoadoutId != loadout.LoadoutId || !item.IsEnabled()) continue;
            var isOverride = LoadoutItem.Parent.TryGetValue(item, out var parent) && LoadoutOverridesGroup.Load(db, parent).IsValid();
            if (winners.TryGetValue(entry.Key, out var current))
            {
                var currentIsOverride = winnerIsOverride.Contains(entry.Key);
                if (currentIsOverride && !isOverride) continue;
                if (currentIsOverride == isOverride && current.Id.Value > entry.Id.Value) continue;
            }
            winners[entry.Key] = entry;
            if (isOverride) winnerIsOverride.Add(entry.Key); else winnerIsOverride.Remove(entry.Key);
        }
        return winners;
    }

    public Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var state = FindState(loadout);
        var text = Render(state.IsValid() ? state.BaseContent : string.Empty, WinningEntries(loadout).ToDictionary(kv => kv.Key, kv => kv.Value.Value));
        var bytes = Encoding.UTF8.GetBytes(text);
        return stream.WriteAsync(bytes, 0, bytes.Length);
    }

    public async Task<ReadOnlyMemory<byte>?> Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var diskText = await reader.ReadToEndAsync();
        var diskBytes = Encoding.UTF8.GetBytes(diskText);
        var diskHash = diskBytes.xxHash3();

        var state = FindState(loadout);
        TDoc disk;
        string baseText;
        try
        {
            disk = Parse(diskText);
            baseText = diskText;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A half-written or hand-broken file: keep the last good base and repair the file from it.
            baseText = state.IsValid() ? state.BaseContent : string.Empty;
            disk = Parse(baseText);
            diskHash = Hash.Zero;
        }

        if (state.IsValid())
        {
            if (state.BaseContent != baseText) tx.Add(state.Id, IntrinsicFileState.BaseContent, baseText);
            if (state.IngestedHash != diskHash) tx.Add(state.Id, IntrinsicFileState.IngestedHash, diskHash);
        }
        else
        {
            _ = new IntrinsicFileState.New(tx) { LoadoutId = loadout.LoadoutId, File = Path, BaseContent = baseText, IngestedHash = diskHash };
        }

        // Owned keys the game changed become External Changes with the game's value.
        var winners = WinningEntries(loadout);
        var effective = winners.ToDictionary(kv => kv.Key, kv => kv.Value.Value);
        LoadoutOverridesGroupId? overrides = null;
        foreach (var (key, entry) in winners)
        {
            if (!TryGet(disk, key, out var onDisk) || onDisk == entry.Value) continue;
            effective[key] = onDisk;
            var isOverride = LoadoutItem.Parent.TryGetValue(entry.AsLoadoutItem(), out var parent) && LoadoutOverridesGroup.Load(loadout.Db, parent).IsValid();
            if (isOverride)
            {
                tx.Add(entry.Id, IntrinsicFileEntry.Value, onDisk);
                continue;
            }
            overrides ??= LoadoutOverrides.GetOrCreate(tx, loadout);
            _ = new IntrinsicFileEntry.New(tx, out var id)
            {
                File = Path, Key = key, Value = onDisk,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = key, LoadoutId = loadout.LoadoutId, ParentId = overrides.Value },
            };
        }

        var rendered = Encoding.UTF8.GetBytes(Render(baseText, effective));
        return rendered.AsSpan().SequenceEqual(diskBytes) ? null : rendered;
    }

    private string Render(string baseText, IReadOnlyDictionary<string, string> values)
    {
        var doc = Parse(baseText);
        foreach (var (key, value) in values) Set(doc, key, value);
        return Serialize(doc);
    }

    private IntrinsicFileState.ReadOnly FindState(Loadout.ReadOnly loadout) =>
        IntrinsicFileState.FindByLoadout(loadout.Db, loadout.LoadoutId).FirstOrDefault(s => s.File == Path);
}
```

`LoadoutOverridesGroupId` → `LoadoutItemGroupId`/`EntityId` conversions: `ParentId = overrides.Value` needs a `LoadoutItemGroupId`; if the implicit conversion is missing, use `ParentId = LoadoutItemGroupId.From(overrides.Value.Value)`. `IsEnabled()` is `NexusMods.Abstractions.Loadouts.Extensions` (`LoadoutItemExtensions.cs:87`). `IntrinsicFileState.ReadOnly` default is invalid (`IsValid()` false), which `FirstOrDefault` relies on.

- [ ] **Step 5: Run to verify they pass**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Synchronizer.Tests --filter "FullyQualifiedName~SettingsIntrinsicFileTests" 2>&1 | grep -E "error CS|Passed!|Failed!|  Failed " | head -8`
Expected: 7 PASS.

- [ ] **Step 6: Commit**

```bash
git add src/NexusMods.Abstractions.Loadouts.Synchronizers tests/NexusMods.DataModel.Synchronizer.Tests/SettingsIntrinsicFileTests.cs tests/Games/NexusMods.Games.TestFramework/AIsolatedGameTest.cs
git commit -m "feat(sync): ASettingsIntrinsicFile: base snapshot, winning entries, ingest to External Changes"
```

---

### Task 3: `UserSettingsFile` (CP2077 JSON format)

**Files:**
- Create: `src/NexusMods.Games.RedEngine/Cyberpunk2077/UserSettingsFile.cs`
- Test: `tests/Games/NexusMods.Games.RedEngine.Tests/UserSettingsFileTests.cs`

**Interfaces:**
- Consumes: `ASettingsIntrinsicFile<JsonNode>` (Task 2).
- Produces: `public class UserSettingsFile(GamePath path) : ASettingsIntrinsicFile<JsonNode>` with key `"<group_name>/<name>"` and JSON-literal values. Tests reach the protected members through a small subclass `Exposed` inside the test file.

- [ ] **Step 1: Write the failing tests**

`tests/Games/NexusMods.Games.RedEngine.Tests/UserSettingsFileTests.cs`:

```csharp
using System.Text.Json.Nodes;
using FluentAssertions;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Sdk.Games;
using Xunit;

namespace NexusMods.Games.RedEngine.Tests;

public class UserSettingsFileTests
{
    private const string Sample = """
        {
          "version": 140,
          "data": [
            { "group_name": "/controls/fpp_camera", "options": [
              { "name": "FPP_MouseX", "type": "float", "value": 5.0, "default_value": 5.0, "min_value": 1.0, "max_value": 30.0, "step_value": 1.0 },
              { "name": "FPP_MouseInvertY", "type": "bool", "value": false, "default_value": false }
            ] },
            { "group_name": "/graphics/advanced", "options": [
              { "name": "DLSS", "type": "name_list", "value": "Auto", "default_value": "Auto", "extra": [1, 2] }
            ] }
          ],
          "unknown_top_level": { "keep": true }
        }
        """;

    private sealed class Exposed() : UserSettingsFile(new GamePath(LocationId.WinePrefix, "x/UserSettings.json"))
    {
        public JsonNode P(string text) => Parse(text);
        public string S(JsonNode doc) => Serialize(doc);
        public bool G(JsonNode doc, string key, out string value) => TryGet(doc, key, out value);
        public void Put(JsonNode doc, string key, string value) => Set(doc, key, value);
    }

    private readonly Exposed _file = new();

    [Fact]
    public void TryGet_ExistingKey_ReturnsTheJsonLiteral()
    {
        var doc = _file.P(Sample);
        _file.G(doc, "/controls/fpp_camera/FPP_MouseX", out var v).Should().BeTrue();
        v.Should().Be("5");
        _file.G(doc, "/graphics/advanced/DLSS", out v).Should().BeTrue();
        v.Should().Be("\"Auto\"");
        _file.G(doc, "/controls/fpp_camera/FPP_MouseInvertY", out v).Should().BeTrue();
        v.Should().Be("false");
    }

    [Fact]
    public void TryGet_MissingKey_IsFalse()
    {
        var doc = _file.P(Sample);
        _file.G(doc, "/graphics/advanced/Nope", out _).Should().BeFalse();
        _file.G(doc, "/nope/DLSS", out _).Should().BeFalse();
    }

    [Fact]
    public void Set_ExistingKey_ReplacesOnlyValue_AndKeepsEverythingElse()
    {
        var doc = _file.P(Sample);
        _file.Put(doc, "/graphics/advanced/DLSS", "\"Off\"");
        var roundTrip = JsonNode.Parse(_file.S(doc))!;

        roundTrip["version"]!.GetValue<int>().Should().Be(140);
        roundTrip["unknown_top_level"]!["keep"]!.GetValue<bool>().Should().BeTrue();
        var dlss = roundTrip["data"]![1]!["options"]![0]!;
        dlss["value"]!.GetValue<string>().Should().Be("Off");
        dlss["type"]!.GetValue<string>().Should().Be("name_list");
        dlss["default_value"]!.GetValue<string>().Should().Be("Auto");
        dlss["extra"]!.AsArray().Count.Should().Be(2);
        roundTrip["data"]![0]!["options"]![0]!["value"]!.GetValue<double>().Should().Be(5.0);
    }

    [Fact]
    public void Set_UnknownOption_AddsItToTheGroup_WithoutInventingAType()
    {
        var doc = _file.P(Sample);
        _file.Put(doc, "/graphics/advanced/NewThing", "true");
        var group = JsonNode.Parse(_file.S(doc))!["data"]![1]!;
        var added = group["options"]!.AsArray().Single(o => o!["name"]!.GetValue<string>() == "NewThing")!;
        added["value"]!.GetValue<bool>().Should().BeTrue();
        added["type"].Should().BeNull();
    }

    [Fact]
    public void Set_UnknownGroup_AddsIt()
    {
        var doc = _file.P(Sample);
        _file.Put(doc, "/mods/Foo", "1");
        var data = JsonNode.Parse(_file.S(doc))!["data"]!.AsArray();
        data.Count.Should().Be(3);
        data[2]!["group_name"]!.GetValue<string>().Should().Be("/mods");
        data[2]!["options"]![0]!["name"]!.GetValue<string>().Should().Be("Foo");
    }

    [Fact]
    public void Set_InvalidLiteral_Throws()
    {
        var doc = _file.P(Sample);
        var act = () => _file.Put(doc, "/graphics/advanced/DLSS", "Off");
        act.Should().Throw<InvalidOperationException>().WithMessage("*/graphics/advanced/DLSS*");
    }

    [Fact]
    public void Set_KeyWithoutSlash_Throws()
    {
        var doc = _file.P(Sample);
        var act = () => _file.Put(doc, "DLSS", "1");
        act.Should().Throw<InvalidOperationException>().WithMessage("*DLSS*");
    }

    [Fact]
    public void Parse_EmptyText_IsTheMinimalDocument()
    {
        var doc = _file.P(string.Empty);
        doc["version"]!.GetValue<int>().Should().Be(140);
        doc["data"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void Serialize_IsIndentedUtf8WithoutBom()
    {
        var text = _file.S(_file.P(Sample));
        text.Should().StartWith("{");
        text.Should().Contain("\n  \"version\": 140");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/Games/NexusMods.Games.RedEngine.Tests --filter "FullyQualifiedName~UserSettingsFileTests" 2>&1 | grep -E "error CS|Passed!|Failed!" | head -3`
Expected: build error, `UserSettingsFile` does not exist.

- [ ] **Step 3: Implement**

`src/NexusMods.Games.RedEngine/Cyberpunk2077/UserSettingsFile.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.RedEngine.Cyberpunk2077;

/// <summary>
/// CP2077's UserSettings.json: { "version": 140, "data": [ { "group_name", "options": [ { "name", "type",
/// "value", "default_value", ... } ] } ] }. A key is "group_name/name" (group names start with "/", so the
/// split is on the LAST slash) and a value is a JSON literal written into "value"; everything else in the
/// option, the group and the document is kept as it was.
/// </summary>
public class UserSettingsFile(GamePath path) : ASettingsIntrinsicFile<JsonNode>(path)
{
    private const int DefaultVersion = 140;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    protected override JsonNode Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject { ["version"] = DefaultVersion, ["data"] = new JsonArray() };
        var node = JsonNode.Parse(text) ?? throw new InvalidOperationException("UserSettings.json vacío");
        if (node["data"] is not JsonArray) throw new InvalidOperationException("UserSettings.json sin 'data'");
        return node;
    }

    protected override string Serialize(JsonNode document) => document.ToJsonString(Indented);

    protected override bool TryGet(JsonNode document, string key, out string value)
    {
        value = string.Empty;
        var (group, name) = Split(key);
        var option = FindGroup(document, group)?["options"]?.AsArray().FirstOrDefault(o => NameOf(o) == name);
        if (option is null) return false;
        value = option["value"]?.ToJsonString() ?? "null";
        return true;
    }

    protected override void Set(JsonNode document, string key, string value)
    {
        var (group, name) = Split(key);
        JsonNode? literal;
        try { literal = JsonNode.Parse(value); }
        catch (JsonException e) { throw new InvalidOperationException($"El valor de '{key}' no es un literal JSON válido: {value}", e); }

        var groupNode = FindGroup(document, group);
        if (groupNode is null)
        {
            groupNode = new JsonObject { ["group_name"] = group, ["options"] = new JsonArray() };
            document["data"]!.AsArray().Add(groupNode);
        }
        var options = groupNode["options"]?.AsArray();
        if (options is null)
        {
            options = new JsonArray();
            groupNode["options"] = options;
        }
        var option = options.FirstOrDefault(o => NameOf(o) == name);
        if (option is null)
        {
            // The game validates by name; no invented "type" or defaults.
            options.Add(new JsonObject { ["name"] = name, ["value"] = literal });
            return;
        }
        option["value"] = literal;
    }

    private static (string Group, string Name) Split(string key)
    {
        var slash = key.LastIndexOf('/');
        if (slash <= 0 || slash == key.Length - 1)
            throw new InvalidOperationException($"La clave '{key}' tiene que ser 'grupo/opción' (ej. /graphics/advanced/DLSS)");
        return (key[..slash], key[(slash + 1)..]);
    }

    private static JsonNode? FindGroup(JsonNode document, string group) =>
        document["data"]?.AsArray().FirstOrDefault(g => g?["group_name"]?.GetValue<string>() == group);

    private static string? NameOf(JsonNode? option) => option?["name"]?.GetValue<string>();
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/Games/NexusMods.Games.RedEngine.Tests --filter "FullyQualifiedName~UserSettingsFileTests" 2>&1 | grep -E "error CS|Passed!|Failed!|  Failed " | head -8`
Expected: 9 PASS. If `TryGet` returns `5.0` instead of `5` for the float, keep the implementation and change the test expectation to whatever `JsonNode.ToJsonString()` emits for `5.0` (it is `5` in .NET 8+); the contract is "the literal as `JsonNode` prints it", and `Set` compares the same way.

- [ ] **Step 5: Commit**

```bash
git add src/NexusMods.Games.RedEngine/Cyberpunk2077/UserSettingsFile.cs tests/Games/NexusMods.Games.RedEngine.Tests/UserSettingsFileTests.cs
git commit -m "feat(cp2077): UserSettings.json as a settings intrinsic file (group_name/name keys)"
```

---

### Task 4: Wire CP2077 and make the synchronizer write after `Ingest`

**Files:**
- Modify: `src/NexusMods.Games.RedEngine/Cyberpunk2077/Cyberpunk2077Game.cs:98-120` (extract `UserSettingsPath`)
- Modify: `src/NexusMods.Games.RedEngine/Cyberpunk2077/Cyberpunk2077Synchronizer.cs` (override `IntrinsicFiles`)
- Modify: `src/NexusMods.Abstractions.Loadouts.Synchronizers/ALoadoutSynchronizer.cs` (`RunActions` ~507, `EnsureDiskChangesStayInside` 583-599, `ActionWriteIntrinsics` 601-616, `AdaptLoadout` 618-631)
- Test: `tests/NexusMods.DataModel.Synchronizer.Tests/IntrinsicSettingsFileTests.cs`

**Interfaces:**
- Consumes: `UserSettingsFile` (Task 3), `ASettingsIntrinsicFile` (Task 2), new `Ingest` return (Task 1).
- Produces: `public static RelativePath Cyberpunk2077Game.UserSettingsPath(AbsolutePath prefix)`; `Cyberpunk2077Synchronizer.IntrinsicFiles(Loadout.ReadOnly)` override; `AdaptLoadout(syncTree, locations, loadout, tx, gameMetadataId)` writes returned bytes and records the disk state; `ActionWriteIntrinsics` records the disk state too.

- [ ] **Step 1: Write the failing integration tests**

`tests/NexusMods.DataModel.Synchronizer.Tests/IntrinsicSettingsFileTests.cs`:

```csharp
using System.Text.Json.Nodes;
using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
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
        var sync = (ALoadoutSynchronizer)GameInstallation.GetGame().Synchronizer;
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
        (await ValueOnDisk("/controls/fpp_camera", "FPP_MouseX")).Should().Be("5");
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
        (await ValueOnDisk("/controls/fpp_camera", "FPP_MouseX")).Should().Be("5", "the base snapshot carried the game's keys");
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
        (await ValueOnDisk("/controls/fpp_camera", "FPP_MouseX")).Should().Be("5");
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

        // "The game" edits the real file behind the link so the sync sees a changed intrinsic
        await outside.WriteAllTextAsync(Original.Replace("\"Auto\"", "\"Quality\"", StringComparison.Ordinal));
        Refresh(ref loadout);
        var act = () => Synchronizer.Synchronize(loadout);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*symlink*");
        (await outside.ReadAllTextAsync()).Should().Contain("\"Quality\"");
        (await outside.ReadAllTextAsync()).Should().NotContain("\"Off\"");
    }
}
```

Note on the symlink test: the whitelist scan never indexes a symlinked whitelisted file (piece 2), so the sync tree sees the intrinsic with no disk state → `WriteIntrinsic`, which `EnsureDiskChangesStayInside` already rejects. The test still proves the post-`Ingest` write path can never reach a symlink because that guard runs before any action. Keep it; it is the regression test for the guard covering `AdaptLoadout` too.

- [ ] **Step 2: Run to verify they fail**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Synchronizer.Tests --filter "FullyQualifiedName~IntrinsicSettingsFileTests" 2>&1 | grep -E "error CS|Passed!|Failed!|  Failed " | head -12`
Expected: build error on `Cyberpunk2077Game.UserSettingsPath`; after stubbing nothing, every test but the symlink one FAILS (no intrinsic declared, so the entry is never written).

- [ ] **Step 3: CP2077 declares the file**

`Cyberpunk2077Game.cs`: replace the body of `GetManagedFiles` and add the helper:

```csharp
    public ImmutableDictionary<LocationId, ImmutableHashSet<RelativePath>> GetManagedFiles(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        if (gameLocatorResult.LinuxCompatabilityDataProvider is not { } linux)
            return ImmutableDictionary<LocationId, ImmutableHashSet<RelativePath>>.Empty;
        return ImmutableDictionary<LocationId, ImmutableHashSet<RelativePath>>.Empty.Add(LocationId.WinePrefix, [UserSettingsPath(linux.WinePrefixDirectoryPath)]);
    }

    /// <summary>
    /// UserSettings.json inside the prefix, relative to the prefix root. One helper for the whitelist and for the
    /// intrinsic file, so both always name the same path (Proton: steamuser; Wine/Lutris: the real user).
    /// </summary>
    public static RelativePath UserSettingsPath(AbsolutePath prefix) =>
        (RelativePath)$"drive_c/users/{WineUserName(prefix)}/AppData/Local/CD Projekt Red/Cyberpunk 2077/UserSettings.json";
```

`Cyberpunk2077Synchronizer.cs`: add

```csharp
    /// <summary>UserSettings.json is generated from the loadout's entries when the prefix location exists.</summary>
    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout)
    {
        var locations = loadout.InstallationInstance.Locations;
        if (!locations.TryGetValue(LocationId.WinePrefix, out var prefix)) return new Dictionary<GamePath, IIntrinsicFile>();
        var path = new GamePath(LocationId.WinePrefix, Cyberpunk2077Game.UserSettingsPath(prefix.Path));
        return new Dictionary<GamePath, IIntrinsicFile> { [path] = new UserSettingsFile(path) };
    }
```

Add `using NexusMods.Abstractions.Loadouts.Synchronizers;` and `using NexusMods.Sdk.Loadouts;` as needed. `Loadout.ReadOnly.InstallationInstance` is what the CLI verbs use (`LoadoutManagementVerbs.cs:59`).

- [ ] **Step 4: Synchronizer writes after `Ingest` and records the disk state**

`ALoadoutSynchronizer.cs`:

1. `EnsureDiskChangesStayInside` (line 585): `const Actions diskChanges = Actions.DeleteFromDisk | Actions.ExtractToDisk | Actions.WriteIntrinsic | Actions.AdaptLoadout;`
2. In `RunActions` (line ~507) pass the metadata id: `await AdaptLoadout(syncTree, locations, loadout, tx, gameMetadataId);` and in `ActionWriteIntrinsics` call add the same argument.
3. Replace both methods:

```csharp
    private async Task ActionWriteIntrinsics(Dictionary<GamePath, SyncNode> syncTree, GameLocations gameLocations, IMainTransaction tx, Loadout.ReadOnly loadout, EntityId gameMetadataId, SynchronizeLoadoutJob? job)
    {
        var intrinsicFiles = IntrinsicFiles(loadout);
        foreach (var (path, node) in syncTree)
        {
            if (!node.Actions.HasFlag(Actions.WriteIntrinsic)) continue;
            if (node.SourceItemType != LoadoutSourceItemType.Intrinsic) throw new Exception("WriteIntrinsic should only be called on intrinsic files");

            var instance = intrinsicFiles[path];
            using var buffer = new MemoryStream();
            await instance.Write(buffer, loadout, syncTree);
            WriteIntrinsicBytes(gameLocations, path, node, buffer.ToArray(), tx, gameMetadataId);
        }
    }

    private async Task AdaptLoadout(Dictionary<GamePath, SyncNode> syncTree, GameLocations gameLocations, Loadout.ReadOnly loadout, IMainTransaction tx, EntityId gameMetadataId)
    {
        var intrinsicFiles = IntrinsicFiles(loadout);
        foreach (var (path, node) in syncTree)
        {
            if (!node.Actions.HasFlag(Actions.AdaptLoadout)) continue;
            if (node.SourceItemType != LoadoutSourceItemType.Intrinsic) throw new Exception("AdaptLoadout should only be called on intrinsic files");

            var instance = intrinsicFiles[path];
            var resolvedPath = gameLocations.ToAbsolutePath(path);
            ReadOnlyMemory<byte>? rewrite;
            await using (var stream = resolvedPath.Read())
                rewrite = await instance.Ingest(stream, loadout, syncTree, tx);
            // Our keys land in the same apply instead of the next one
            if (rewrite is { } bytes)
                WriteIntrinsicBytes(gameLocations, path, node, bytes.ToArray(), tx, gameMetadataId);
        }
    }

    /// <summary>
    /// Writes a generated intrinsic file and records its disk state, like ActionExtractToDisk does, so the
    /// next sync compares against what was written instead of ingesting our own output as a game change.
    /// </summary>
    private static void WriteIntrinsicBytes(GameLocations gameLocations, GamePath path, SyncNode node, byte[] bytes, ITransaction tx, EntityId gameMetadataId)
    {
        var resolvedPath = gameLocations.ToAbsolutePath(path);
        resolvedPath.Parent.CreateDirectory();
        using (var stream = resolvedPath.Create())
        {
            stream.SetLength(0);
            stream.Write(bytes);
        }
        var hash = bytes.xxHash3();
        var size = Size.FromLong(bytes.Length);
        var writeTimeUtc = new DateTimeOffset(resolvedPath.FileInfo.LastWriteTimeUtc);
        if (node.HaveDisk)
        {
            var id = node.Disk.EntityId;
            tx.Add(id, DiskStateEntry.Hash, hash);
            tx.Add(id, DiskStateEntry.Size, size);
            tx.Add(id, DiskStateEntry.LastModified, writeTimeUtc);
        }
        else
        {
            _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
            {
                Path = path.ToGamePathParentTuple(gameMetadataId),
                Hash = hash,
                Size = size,
                LastModified = writeTimeUtc,
                GameId = gameMetadataId,
            };
        }
    }
```

`RunActions` for a `GameInstallation` (line ~748, the no-loadout variant) does not call these; leave it. Check `gameMetadataId`'s declared type in `RunActions` (`EntityId` from `loadout.InstallationId` or similar) and match the parameter.

- [ ] **Step 5: Run the new tests, then the whole Synchronizer and RedEngine projects**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Synchronizer.Tests --filter "FullyQualifiedName~IntrinsicSettingsFileTests" 2>&1 | grep -E "error CS|Passed!|Failed!|  Failed " | head -12`
Expected: 9 PASS. Then:
`DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Synchronizer.Tests --filter "RequiresNetworking!=True&FlakeyTest!=True" 2>&1 | grep -E "Passed!|Failed!"; DOTNET_ROLL_FORWARD=Major dotnet test tests/Games/NexusMods.Games.RedEngine.Tests --filter "RequiresNetworking!=True&FlakeyTest!=True" 2>&1 | grep -E "Passed!|Failed!"; DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Tests --filter "FullyQualifiedName~SynchronizerRuleTests" 2>&1 | grep -E "Passed!|Failed!"; git status --short | grep -E "verified|received"`
Expected: all green, no snapshot files changed (criteria 7 and 8). If `WinePrefixLocationTests.ResetRestoresTheSettingsAndLeavesTheRest` fails because `UserSettings.json` is now intrinsic (its mod file is ignored in favour of the generated content), that test's premise changed: `AddModAsync` putting a *file* at the intrinsic path is no longer a supported setup. Change that test to use a mod entry (`IntrinsicFileEntry`) instead of a mod file and keep its assertions (reset restores `original`, `CrashInfo.json` intact). Ledger the ruling.

- [ ] **Step 6: Prove the symlink test bites**

Temporarily remove `| Actions.AdaptLoadout` from `diskChanges` AND make `AdaptLoadout` write unconditionally (`WriteIntrinsicBytes(...)` even when `rewrite` is null, with the rendered base) — the simplest way is to temporarily skip the `EnsureDiskChangesStayInside` call; run `--filter "FullyQualifiedName~SettingsFileThatIsASymlink"`. Expected: FAIL (the outside file gets "Off"). Restore, rerun, PASS. If the test does not fail even without the guard, the scan never let the intrinsic through: strengthen the test by writing the symlink *after* the loadout was managed with a real file (so the disk state has an entry), then rerun both ways.

- [ ] **Step 7: Commit**

```bash
git add src/NexusMods.Games.RedEngine/Cyberpunk2077 src/NexusMods.Abstractions.Loadouts.Synchronizers/ALoadoutSynchronizer.cs tests/NexusMods.DataModel.Synchronizer.Tests
git commit -m "feat(sync): write intrinsic files after ingest; CP2077 declares UserSettings.json as one"
```

---

### Task 5: CLI `loadout settings set|list`

**Files:**
- Create: `src/NexusMods.Abstractions.Loadouts.Synchronizers/LoadoutSettings.cs`
- Modify: `src/NexusMods.DataModel/CommandLine/Verbs/LoadoutManagementVerbs.cs:33-51` (modules + verbs)
- Test: `tests/NexusMods.DataModel.Synchronizer.Tests/LoadoutSettingsTests.cs`

**Interfaces:**
- Consumes: `ISettingsIntrinsicFile.TryValidate` (Task 2), `ALoadoutSynchronizer.IntrinsicFiles` (Task 4).
- Produces: `public static class LoadoutSettings` with `const string GroupName = "Ajustes"`, `static bool TryResolveFile(Loadout.ReadOnly loadout, string? fileArg, out GamePath path, out IIntrinsicFile file, out string? error)`, `static Task<IntrinsicFileEntry.ReadOnly> Upsert(IConnection connection, Loadout.ReadOnly loadout, GamePath file, string key, string value)`, `static IEnumerable<(GamePath File, string Key, string Value, string Group, bool Wins)> List(Loadout.ReadOnly loadout)`.

- [ ] **Step 1: Write the failing tests**

`tests/NexusMods.DataModel.Synchronizer.Tests/LoadoutSettingsTests.cs`:

```csharp
using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
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
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Synchronizer.Tests --filter "FullyQualifiedName~LoadoutSettingsTests" 2>&1 | grep -E "error CS|Passed!|Failed!" | head -3`
Expected: build error, `LoadoutSettings` does not exist.

- [ ] **Step 3: Implement the helper**

`src/NexusMods.Abstractions.Loadouts.Synchronizers/LoadoutSettings.cs`:

```csharp
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

/// <summary>Entries of intrinsic settings files set by hand (CLI), kept in the loadout's "Ajustes" group.</summary>
public static class LoadoutSettings
{
    public const string GroupName = "Ajustes";

    /// <summary>
    /// Resolves which intrinsic file an entry targets: <paramref name="fileArg"/> as "Location:relative/path",
    /// or the game's only intrinsic file when omitted.
    /// </summary>
    public static bool TryResolveFile(Loadout.ReadOnly loadout, string? fileArg, out GamePath path, out IIntrinsicFile file, out string? error)
    {
        path = default;
        file = null!;
        error = null;
        var intrinsics = ((ALoadoutSynchronizer)loadout.InstallationInstance.GetGame().Synchronizer).IntrinsicFiles(loadout);
        if (string.IsNullOrWhiteSpace(fileArg))
        {
            if (intrinsics.Count != 1)
            {
                error = intrinsics.Count == 0
                    ? "Este juego no declara archivos de configuración gestionados (¿falta el prefix de Wine?)"
                    : "Hay varios archivos de configuración; indicá cuál con -f Ubicación:ruta. Opciones: " + string.Join(", ", intrinsics.Keys);
                return false;
            }
            (path, file) = (intrinsics.Keys.First(), intrinsics.Values.First());
            return true;
        }
        var colon = fileArg.IndexOf(':');
        if (colon <= 0)
        {
            error = $"'{fileArg}' no tiene la forma Ubicación:ruta (ej. WinePrefix:drive_c/...)";
            return false;
        }
        path = new GamePath(LocationId.From(fileArg[..colon]), (RelativePath)fileArg[(colon + 1)..]);
        if (!intrinsics.TryGetValue(path, out file!))
        {
            error = $"'{fileArg}' no es un archivo de configuración gestionado. Opciones: " + string.Join(", ", intrinsics.Keys);
            return false;
        }
        return true;
    }

    public static async Task<IntrinsicFileEntry.ReadOnly> Upsert(IConnection connection, Loadout.ReadOnly loadout, GamePath file, string key, string value)
    {
        var db = connection.Db;
        using var tx = connection.BeginTransaction();
        var group = LoadoutItem.FindByLoadout(db, loadout.LoadoutId)
            .Where(i => i.Name == GroupName && !i.HasParent())
            .Select(i => (EntityId)i.Id)
            .FirstOrDefault();
        if (group == default)
        {
            var created = new LoadoutItemGroup.New(tx, out var groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = GroupName, LoadoutId = loadout.LoadoutId },
            };
            group = created.Id;
        }

        var existing = IntrinsicFileEntry.FindByFile(db, file)
            .FirstOrDefault(e => e.Key == key && e.AsLoadoutItem().LoadoutId == loadout.LoadoutId
                                 && LoadoutItem.Parent.TryGetValue(e.AsLoadoutItem(), out var p) && p.Value == group);
        if (existing.IsValid())
        {
            if (existing.Value != value) tx.Add(existing.Id, IntrinsicFileEntry.Value, value);
            await tx.Commit();
            return IntrinsicFileEntry.Load(connection.Db, existing.Id);
        }

        var entry = new IntrinsicFileEntry.New(tx, out var id)
        {
            File = file, Key = key, Value = value,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = key, LoadoutId = loadout.LoadoutId, ParentId = LoadoutItemGroupId.From(group) },
        };
        var result = await tx.Commit();
        return result.Remap(entry);
    }

    public static IEnumerable<(GamePath File, string Key, string Value, string Group, bool Wins)> List(Loadout.ReadOnly loadout)
    {
        var db = loadout.Db;
        var intrinsics = ((ALoadoutSynchronizer)loadout.InstallationInstance.GetGame().Synchronizer).IntrinsicFiles(loadout);
        foreach (var (path, file) in intrinsics)
        {
            var winners = file is ISettingsIntrinsicFile settings
                ? settings.WinningEntries(loadout).ToDictionary(kv => kv.Key, kv => kv.Value.Id)
                : new Dictionary<string, EntityId>();
            foreach (var entry in IntrinsicFileEntry.FindByFile(db, path).Where(e => e.AsLoadoutItem().LoadoutId == loadout.LoadoutId))
            {
                var item = entry.AsLoadoutItem();
                var group = LoadoutItem.Parent.TryGetValue(item, out var parent) ? LoadoutItem.Load(db, parent).Name : "(sin grupo)";
                yield return (path, entry.Key, entry.Value, group, winners.TryGetValue(entry.Key, out var w) && w == entry.Id);
            }
        }
    }
}
```

- [ ] **Step 4: Add the verbs**

`LoadoutManagementVerbs.cs`: add `.AddModule("loadout settings", "Entradas de archivos de configuración que el loadout posee (UserSettings.json en CP2077)")` next to the other modules, register `.AddVerb(() => SettingsSet)` and `.AddVerb(() => SettingsList)`, and add:

```csharp
    [Verb("loadout settings set", "Fija el valor de una clave de un archivo de configuración gestionado")]
    private static async Task<int> SettingsSet([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout")] Loadout.ReadOnly loadout,
        [Option("k", "key", "Clave (CP2077: grupo/opción, ej. /graphics/advanced/DLSS)")] string key,
        [Option("v", "value", "Valor como literal del formato (CP2077: JSON, ej. \"Off\", 5.0, true)")] string value,
        [Option("f", "file", "Archivo como Ubicación:ruta; por defecto el único del juego", isOptional: true)] string? file,
        [Injected] IConnection connection)
    {
        if (!LoadoutSettings.TryResolveFile(loadout, file, out var path, out var intrinsic, out var error))
        {
            await renderer.Error("{0}", error!);
            return -1;
        }
        if (intrinsic is ISettingsIntrinsicFile settings && !settings.TryValidate(key, value, out var invalid))
        {
            await renderer.Error("{0}", invalid!);
            return -1;
        }
        var entry = await LoadoutSettings.Upsert(connection, loadout, path, key, value);
        await renderer.Text("{0} = {1} en {2} (grupo '{3}'). Aplicá el loadout para escribirlo.", entry.Key, entry.Value, path, LoadoutSettings.GroupName);
        return 0;
    }

    [Verb("loadout settings list", "Lista las entradas de archivos de configuración del loadout")]
    private static async Task<int> SettingsList([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout")] Loadout.ReadOnly loadout)
    {
        await LoadoutSettings.List(loadout)
            .Select(r => (r.File.ToString(), r.Key, r.Value, r.Group, r.Wins ? "sí" : ""))
            .RenderTable(renderer, "Archivo", "Clave", "Valor", "Grupo", "Gana");
        return 0;
    }
```

If `renderer.Error`/`renderer.Text` signatures differ (`SetVersion` at line 54 shows `renderer.Error("Version {0} not found", version)`), match them.

- [ ] **Step 5: Run to verify they pass, and build the app**

Run: `cd ~/Repos/tModManager && DOTNET_ROLL_FORWARD=Major dotnet test tests/NexusMods.DataModel.Synchronizer.Tests --filter "FullyQualifiedName~LoadoutSettingsTests" 2>&1 | grep -E "error CS|Passed!|Failed!|  Failed " | head -8 && DOTNET_ROLL_FORWARD=Major dotnet build src/NexusMods.App/NexusMods.App.csproj -p:TreatWarningsAsErrors=true 2>&1 | tail -3`
Expected: 5 PASS, 0 warnings, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/NexusMods.Abstractions.Loadouts.Synchronizers/LoadoutSettings.cs src/NexusMods.Abstractions.Loadouts.Synchronizers/ASettingsIntrinsicFile.cs src/NexusMods.DataModel/CommandLine/Verbs/LoadoutManagementVerbs.cs tests/NexusMods.DataModel.Synchronizer.Tests/LoadoutSettingsTests.cs
git commit -m "feat(cli): loadout settings set/list for intrinsic file entries"
```

---

### Task 6: Docs, full verification, PR

**Files:**
- Modify: `TODO.md` (status line 9, pieces table row 4 at line ~178, "Para probar en Linux", "Errores conocidos" one line for the documented limitation)
- Modify: `CLAUDE.md` (Loadout & Synchronization paragraph: one sentence on intrinsic settings files; Fork-Specific Features item 12)
- Modify: `docs/superpowers/specs/2026-10-10-intrinsic-settings-file-design.md` (status line)

- [ ] **Step 1: `TODO.md`**

- Line 9 "Próximo": `2. Pieza 5 (load order que se escribe a archivo) o pieza 6 (requisitos del prefix como datos). Las piezas 2, 3 y 4 están hechas (PRs #73, #74, #NN); la 4 queda por probar con el juego real.`
- Pieces table row 4: `| 4 | Primer uso real de \`IIntrinsicFile\` (hecha 2026-10-10, PR #NN, prueba con el juego pendiente; \`ASettingsIntrinsicFile\` + \`IntrinsicFileEntry\`/\`IntrinsicFileState\`, \`Ingest\` → External Changes y reescritura en el mismo apply; CP2077: \`UserSettings.json\`; spec en \`docs/superpowers/specs/2026-10-10-intrinsic-settings-file-design.md\`) | \`UserSettings.json\` (\`loadout settings set\`) | \`mods.settings\`, \`dx12user.settings\`/\`input.settings\` (formato INI pendiente) | \`plugins.txt\` (formato de líneas pendiente) |`
- "Para probar en Linux": `- [ ] **Pieza 4 con el juego** (PR #NN, solo tests): \`loadout settings set -l <loadout> -k /graphics/advanced/DLSS -v '"Off"'\` (o cualquier opción visible en el menú), aplicar, abrir el juego y ver el ajuste; cambiarlo desde el menú del juego, salir, aplicar: aparece en External Changes con el valor del juego y el log dice qué reescribió; borrar esa entrada y aplicar vuelve al valor fijado; borrar \`UserSettings.json\`, aplicar, regenerado`
- "Errores conocidos y deuda": `- [ ] **Pieza 4, límite conocido:** al deshabilitar un mod con entradas, la clave conserva el último valor (la base absorbe lo que el juego reescribió) hasta que el juego o el usuario la cambien; revertir de verdad pediría guardar el valor previo al primer Write por clave. Y: formatos INI/líneas, UI de entradas, orden por load order entre entradas, productores desde instaladores/colecciones`

Use `#NN` now; Step 5 replaces it.

- [ ] **Step 2: `CLAUDE.md`**

In "Loadout & Synchronization", after the data hierarchy line add: `Intrinsic settings files (\`ASettingsIntrinsicFile<TDoc>\`): a game declares files it rewrites (CP2077: \`UserSettings.json\` via \`Cyberpunk2077Synchronizer.IntrinsicFiles\`); the loadout owns keys through \`IntrinsicFileEntry\` items (any group), the last ingested disk text is the base (\`IntrinsicFileState\`), apply writes base + winning entries, and a game-made change to an owned key becomes an External Change entry that wins. \`IIntrinsicFile.Ingest\` returns the bytes to rewrite in the same apply.`

In "Fork-Specific Features" add item 12 with the same content in one line, naming `loadout settings set|list`.

- [ ] **Step 3: Spec status**

`Estado: implementado en la rama \`feat/intrinsic-settings-file\` (PR #NN), prueba con el juego pendiente`.

- [ ] **Step 4: Full verification**

Run: `cd ~/Repos/tModManager && export DOTNET_ROLL_FORWARD=Major PATH=$HOME/.dotnet:$PATH && (printf '4\n0\n' | ./dev.sh) > /tmp/claude-1000/-home-tatoh-Repos-tModManager/407fb78a-dedf-4863-9c2d-1f7196dd5e3d/scratchpad/devsh4.log 2>&1; grep -E 'Passed!|Failed!|  Failed |Test run summary|Todos los tests|Proyectos con fallas' /tmp/claude-1000/-home-tatoh-Repos-tModManager/407fb78a-dedf-4863-9c2d-1f7196dd5e3d/scratchpad/devsh4.log | sed 's/\x1b\[[0-9;]*m//g; s/\[.*//'`
Expected: every project green, "Todos los tests pasaron". `SchemaFingerprintHasntChanged` WILL fail because two models were added: diff `tests/NexusMods.DataModel.SchemaVersions.Tests/Schema.received.md` against `Schema.verified.md`; if the diff is exactly the new `IntrinsicFileEntry/*` and `IntrinsicFileState/*` attributes plus the fingerprint/total lines, accept it with `cp Schema.received.md Schema.verified.md && rm Schema.received.md` and rerun that test. Any other difference: stop and report.

- [ ] **Step 5: Commit docs, push, open the PR**

```bash
git add TODO.md CLAUDE.md docs/superpowers/specs/2026-10-10-intrinsic-settings-file-design.md tests/NexusMods.DataModel.SchemaVersions.Tests/Schema.verified.md
git commit -m "docs: piece 4 (intrinsic settings file) done; schema snapshot with the two new models"
git push -u origin feat/intrinsic-settings-file
gh pr create --title "feat: intrinsic settings files with per-mod entries and ingest (piece 4)" --body-file /tmp/claude-1000/-home-tatoh-Repos-tModManager/407fb78a-dedf-4863-9c2d-1f7196dd5e3d/scratchpad/pr-body-4.md
```

Write `pr-body-4.md` first: what changed (one bullet per task), how it was verified (dev.sh option 4 summary), the documented limitation (disabled mod keeps the last value), what is pending (real-game test, listed in `TODO.md`), out of scope (spec's list). No AI attribution footer. After `gh pr create` prints the number, replace `#NN` in `TODO.md` and the spec, commit `docs: PR number for piece 4`, push. Do not merge: the user merges on explicit order.
