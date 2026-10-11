# Load order escrito a archivo (pieza 5) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Una variedad de sort order declara el archivo donde se escribe su orden; el core lo escribe como intrínseco en cada apply; CP2077 lo prueba con `drive_c/modlist.txt` de REDmod.

**Architecture:** `ISortOrderFile` (ruta + `Render`) vive en `Abstractions.Loadouts.Synchronizers` y lo implementa `RedModSortOrderVariety`, registrado también como `ISortOrderFile` en DI. `ALoadoutSynchronizer.IntrinsicFiles` por defecto envuelve cada `ISortOrderFile` del juego en un `SortOrderIntrinsicFile` (loadout gana, sin External Change) pasando por `DeclareIntrinsic`, que concentra las guardas que hoy están en el synchronizer de CP2077. `RedModDeployTool` deja de generar el modlist y solo corre el deploy.

**Tech Stack:** .NET 10 / C#, MnemonicDB, xUnit v3 + FluentAssertions + Verify, fixture `ACyberpunkIsolatedGameTest` (`NexusMods.Games.TestFramework`).

**Spec:** `docs/superpowers/specs/2026-10-11-sort-order-file-design.md`

## Global Constraints

- Nada CP-específico fuera de `NexusMods.Games.RedEngine`; nada en el core asume Nexus.
- `dotnet build -p:TreatWarningsAsErrors=true` con 0 warnings (`.globalconfig` como errores; `CS4014`, `CS8509`, etc.).
- Nunca `dotnet test` sobre la solución entera: un proyecto por vez.
- Nunca `DeleteDirectory(recursive: true)`; paths de datos no confiables pasan por `SafePath`.
- Toda acción destructiva lleva un test con un symlink que apunta afuera, y el test tiene que fallar sin el fix.
- Línea de comandos del deploy intacta: `deploy -force -modlist=C:\modlist.txt` vía `deploy_redmod.bat`.
- REDmod solo acepta CRLF en el modlist.
- Los `.verified.` de `RedModDeployToolTests` no se tocan.
- Commits sin co-autor ni footer de IA.

## Review Focus

1. **Prefix borrado entre dos applies** (Storage Manager "Borrar prefix"): la base del disco todavía tiene `drive_c/modlist.txt`; el apply siguiente no debe fallar ni intentar escribir en una carpeta que no existe. → Task 5 (`PrefixDeletedAfterApply_SyncDoesNotThrow`).
2. **`modlist.txt` preexistente de otro manager** (Vortex): es un original; tiene que respaldarse y volver byte a byte al dejar de gestionar. → Task 5 (`PreexistingModlist_IsBackedUpAndRestoredOnUnManage`).
3. **Loadout sin REDmods pero con prefix**: `Render` devuelve vacío; no debe crearse un `modlist.txt` vacío de la nada ni correr el deploy. → Task 4 (`SortOrderIntrinsicFile` no inventa el archivo con texto vacío) y Task 5 (`NoRedMods_NoFileIsCreated`).
4. **Edición a mano con otro fin de línea** (`\n` en vez de `\r\n`): el apply tiene que restaurar CRLF, sin External Change. → Task 5 (`HandEdit_IsOverwrittenWithoutExternalChange`).
5. **Symlink en `drive_c/modlist.txt`** hacia un archivo de afuera: no se escribe a través, el de afuera queda intacto, el apply sigue. Debe fallar sin la guarda. → Task 5 (`SymlinkedModlist_IsNeverWrittenThrough`) + verificación negativa en Task 7.

---

### Task 1: `ISortOrderFile` + `IIntrinsicFile.IsUpToDate` default

**Files:**
- Create: `src/NexusMods.Abstractions.Loadouts.Synchronizers/ISortOrderFile.cs`
- Modify: `src/NexusMods.Abstractions.Loadouts.Synchronizers/IIntrinsicFile.cs`
- Modify: `src/NexusMods.Abstractions.Loadouts.Synchronizers/ASettingsIntrinsicFile.cs:10-18` (quitar `IsUpToDate` de `ISettingsIntrinsicFile`, queda heredado)
- Modify: `src/NexusMods.Abstractions.Loadouts.Synchronizers/ALoadoutSynchronizer.cs:1223-1229` (`IntrinsicIsUpToDate`)

**Interfaces:**
- Produces: `ISortOrderFile { IEnumerable<GameId> GameIds; GamePath Path; string Render(Loadout.ReadOnly loadout, IDb db); }`
- Produces: `IIntrinsicFile.IsUpToDate(string diskText, Loadout.ReadOnly loadout)` (default `false`).

- [ ] **Step 1: Crear `ISortOrderFile.cs`**

```csharp
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
```

- [ ] **Step 2: Agregar el default a `IIntrinsicFile`**

En `IIntrinsicFile.cs`, después de `Ingest`:

```csharp
    /// <summary>
    /// True when the file on disk already holds what this loadout would write, so "WriteIntrinsic" is not a
    /// pending change. Default false: an intrinsic that cannot tell always counts as pending.
    /// </summary>
    public bool IsUpToDate(string diskText, Loadout.ReadOnly loadout) => false;
```

- [ ] **Step 3: Quitar la declaración duplicada de `ISettingsIntrinsicFile`**

En `ASettingsIntrinsicFile.cs` borrar las dos líneas:

```csharp
    /// <summary>True when every owned key already has its value in <paramref name="diskText"/> (always true with nothing owned).</summary>
    bool IsUpToDate(string diskText, Loadout.ReadOnly loadout);
```

La implementación `public bool IsUpToDate(...)` de `ASettingsIntrinsicFile<TDoc>` queda y pasa a implementar el miembro de `IIntrinsicFile`.

- [ ] **Step 4: `IntrinsicIsUpToDate` usa la interfaz base**

En `ALoadoutSynchronizer.cs` reemplazar el método:

```csharp
    private static bool IntrinsicIsUpToDate(GamePath path, Loadout.ReadOnly loadout, Dictionary<GamePath, IIntrinsicFile> intrinsics)
    {
        if (!intrinsics.TryGetValue(path, out var file)) return false;
        var resolved = loadout.InstallationInstance.Locations.ToAbsolutePath(path);
        var text = resolved.FileExists ? File.ReadAllText(resolved.ToString()) : string.Empty;
        return file.IsUpToDate(text, loadout);
    }
```

- [ ] **Step 5: Build y tests de pieza 4 siguen verdes**

Run: `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROLL_FORWARD=Major; dotnet build -p:TreatWarningsAsErrors=true 2>&1 | grep -E 'error|Warning\(s\)|Error\(s\)' | grep -v NU19`
Expected: `0 Warning(s)`, `0 Error(s)`.

Run: `dotnet test tests/NexusMods.DataModel.Synchronizer.Tests --no-build --filter "FullyQualifiedName~IntrinsicSettingsFileTests"`
Expected: todos pasan (incluye `ShouldSynchronize_IsFalseAfterApply_AndTrueAfterANewEntryValue`).

- [ ] **Step 6: Commit**

```bash
git add src/NexusMods.Abstractions.Loadouts.Synchronizers
git commit -m "feat(sync): ISortOrderFile and IsUpToDate on every intrinsic file"
```

---

### Task 2: `SortOrderIntrinsicFile` (adaptador, tests unitarios puros)

**Files:**
- Create: `src/NexusMods.Abstractions.Loadouts.Synchronizers/SortOrderIntrinsicFile.cs`
- Test: `tests/NexusMods.DataModel.Tests/SortOrderIntrinsicFileTests.cs`

**Interfaces:**
- Consumes: `ISortOrderFile` (Task 1).
- Produces: `sealed class SortOrderIntrinsicFile(ISortOrderFile source) : IIntrinsicFile` con `Path => source.Path`.

- [ ] **Step 1: Escribir el test que falla**

`tests/NexusMods.DataModel.Tests/SortOrderIntrinsicFileTests.cs` (proyecto xUnit sin juego; `Loadout.ReadOnly` default alcanza porque el fake ignora el loadout):

```csharp
using System.Text;
using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using Xunit;

namespace NexusMods.DataModel.Tests;

public class SortOrderIntrinsicFileTests
{
    private sealed class FakeOrder(string text) : ISortOrderFile
    {
        public string Text { get; set; } = text;
        public IEnumerable<GameId> GameIds => [GameId.From("Stub")];
        public GamePath Path => new(LocationId.Game, "order.txt");
        public string Render(Loadout.ReadOnly loadout, IDb db) => Text;
    }

    private static Stream Disk(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Write_IsTheRenderedText()
    {
        var file = new SortOrderIntrinsicFile(new FakeOrder("a\r\nb"));
        using var stream = new MemoryStream();

        await file.Write(stream, default, new Dictionary<GamePath, SyncNode>());

        Encoding.UTF8.GetString(stream.ToArray()).Should().Be("a\r\nb");
    }

    [Fact]
    public async Task Ingest_DiskMatches_ReturnsNull()
    {
        var file = new SortOrderIntrinsicFile(new FakeOrder("a\r\nb"));

        var rewrite = await file.Ingest(Disk("a\r\nb"), default, new Dictionary<GamePath, SyncNode>(), null!);

        rewrite.Should().BeNull();
    }

    [Fact]
    public async Task Ingest_DiskDiffers_ReturnsTheRenderedText_TheLoadoutWins()
    {
        var file = new SortOrderIntrinsicFile(new FakeOrder("a\r\nb"));

        var rewrite = await file.Ingest(Disk("b\na"), default, new Dictionary<GamePath, SyncNode>(), null!);

        rewrite.Should().NotBeNull();
        Encoding.UTF8.GetString(rewrite!.Value.ToArray()).Should().Be("a\r\nb");
    }

    [Fact]
    public async Task Ingest_NothingToOrderAndNoFile_ReturnsNull()
    {
        // An empty order never creates an empty file out of nothing
        var file = new SortOrderIntrinsicFile(new FakeOrder(string.Empty));

        var rewrite = await file.Ingest(Stream.Null, default, new Dictionary<GamePath, SyncNode>(), null!);

        rewrite.Should().BeNull();
    }

    [Fact]
    public async Task Ingest_NothingToOrderButFileHasContent_EmptiesIt()
    {
        var file = new SortOrderIntrinsicFile(new FakeOrder(string.Empty));

        var rewrite = await file.Ingest(Disk("stale"), default, new Dictionary<GamePath, SyncNode>(), null!);

        rewrite.Should().NotBeNull();
        rewrite!.Value.Length.Should().Be(0);
    }

    [Fact]
    public void IsUpToDate_ComparesTextExactly()
    {
        var file = new SortOrderIntrinsicFile(new FakeOrder("a\r\nb"));

        file.IsUpToDate("a\r\nb", default).Should().BeTrue();
        file.IsUpToDate("a\nb", default).Should().BeFalse("REDmod needs CRLF: a hand edit with LF is pending");
        file.IsUpToDate(string.Empty, default).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Correrlo y ver que falla**

Run: `dotnet build tests/NexusMods.DataModel.Tests 2>&1 | grep -E 'error' | head -3`
Expected: `error CS0246: ... 'SortOrderIntrinsicFile' could not be found`.

- [ ] **Step 3: Implementar `SortOrderIntrinsicFile.cs`**

```csharp
using System.Text;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Writes an <see cref="ISortOrderFile"/> as an intrinsic file. The loadout is the source of truth:
/// a file that differs from the rendered order is rewritten in the same apply, no External Change is
/// created (the game never writes this file). An empty order never creates a file out of nothing.
/// </summary>
public sealed class SortOrderIntrinsicFile(ISortOrderFile source) : IIntrinsicFile
{
    public GamePath Path => source.Path;

    public Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var bytes = Encoding.UTF8.GetBytes(source.Render(loadout, loadout.Db));
        return stream.WriteAsync(bytes, 0, bytes.Length);
    }

    public async Task<ReadOnlyMemory<byte>?> Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var diskText = await reader.ReadToEndAsync();
        var wanted = source.Render(loadout, loadout.Db);
        if (diskText == wanted) return null;
        if (wanted.Length == 0 && diskText.Length == 0) return null;
        return new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(wanted));
    }

    public bool IsUpToDate(string diskText, Loadout.ReadOnly loadout) => diskText == source.Render(loadout, loadout.Db);
}
```

Nota: con `Stream.Null` (archivo inexistente) `diskText` es vacío; si `wanted` también es vacío no se devuelve nada y el core no crea el archivo. Si el archivo existe con contenido y el orden es vacío, se devuelve un arreglo vacío y el core lo deja en cero bytes.

- [ ] **Step 4: Tests en verde**

Run: `dotnet test tests/NexusMods.DataModel.Tests --filter "FullyQualifiedName~SortOrderIntrinsicFileTests"`
Expected: 6 passed.

`loadout.Db` con `default(Loadout.ReadOnly)`: si lanza `NullReferenceException` en `Write`/`Ingest`, cambiar el fake para no depender del loadout no alcanza porque el adaptador llama `loadout.Db`. En ese caso el test construye un loadout real: mover la clase de test a `tests/NexusMods.DataModel.Synchronizer.Tests/` heredando `ACyberpunkIsolatedGameTest<SortOrderIntrinsicFileTests>` y usar `await CreateLoadout()` en lugar de `default`. Preferir primero el proyecto sin juego.

- [ ] **Step 5: Commit**

```bash
git add src/NexusMods.Abstractions.Loadouts.Synchronizers/SortOrderIntrinsicFile.cs tests/NexusMods.DataModel.Tests/SortOrderIntrinsicFileTests.cs
git commit -m "feat(sync): SortOrderIntrinsicFile, the loadout's order written as an intrinsic"
```

---

### Task 3: `DeclareIntrinsic` en la base y `IntrinsicFiles` por defecto desde `ISortOrderFile`

**Files:**
- Modify: `src/NexusMods.Abstractions.Loadouts.Synchronizers/ALoadoutSynchronizer.cs:1772-1781` (`IntrinsicFiles`) y campos cerca de `:50-57`
- Modify: `src/NexusMods.Games.RedEngine/Cyberpunk2077/Cyberpunk2077Synchronizer.cs:18-19,65-85`

**Interfaces:**
- Consumes: `ISortOrderFile`, `SortOrderIntrinsicFile` (Tasks 1–2).
- Produces: `protected IIntrinsicFile? DeclareIntrinsic(Loadout.ReadOnly loadout, GamePath path, Func<GamePath, IIntrinsicFile> factory)`; `IntrinsicFiles` base no vacío.

- [ ] **Step 1: Test existente como red: `IntrinsicSettingsFileTests.SettingsFileThatIsASymlink_IsNeverWritten` y `WinePrefixLocationTests` deben seguir pasando después del cambio** (no hay test nuevo en esta task: el comportamiento nuevo se prueba en Task 5 con REDmod).

- [ ] **Step 2: Campos y `DeclareIntrinsic` en `ALoadoutSynchronizer`**

Agregar junto a los campos privados (después de `_gameRegistry`):

```csharp
    private readonly Lazy<ISortOrderFile[]> _sortOrderFiles;
    private readonly HashSet<GamePath> _warnedSymlinkedIntrinsics = [];
```

En el constructor principal (el que recibe `IServiceProvider serviceProvider, ILogger logger, ...`), después de `_serviceProvider = serviceProvider;`:

```csharp
        // Lazy: a variety takes IServiceProvider itself, resolving it here would cycle on construction
        _sortOrderFiles = new Lazy<ISortOrderFile[]>(() => serviceProvider.GetServices<ISortOrderFile>().ToArray());
```

Reemplazar `IntrinsicFiles` por:

```csharp
    /// <summary>
    /// Files generated from the loadout at sync time (load orders, settings): never backed up, never part of
    /// the loadout as files. By default every <see cref="ISortOrderFile"/> registered for this game; a game
    /// adds its own through <see cref="DeclareIntrinsic"/>.
    /// </summary>
    public virtual Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout)
    {
        var result = new Dictionary<GamePath, IIntrinsicFile>();
        var gameId = loadout.InstallationInstance.Game.GameId;
        foreach (var order in _sortOrderFiles.Value)
        {
            if (!order.GameIds.Contains(gameId)) continue;
            if (DeclareIntrinsic(loadout, order.Path, _ => new SortOrderIntrinsicFile(order)) is { } file)
                result[order.Path] = file;
        }
        return result;
    }

    /// <summary>
    /// Builds an intrinsic for <paramref name="path"/> when it can be written: its location is declared and
    /// exists on disk (a Wine prefix may not, yet or any more), the path is managed (whitelist), and it is
    /// neither a symlink nor under one (never written through; said once per path, the rest of the loadout
    /// still applies). Null otherwise.
    /// </summary>
    protected IIntrinsicFile? DeclareIntrinsic(Loadout.ReadOnly loadout, GamePath path, Func<GamePath, IIntrinsicFile> factory)
    {
        var locations = loadout.InstallationInstance.Locations;
        if (!locations.TryGetValue(path.LocationId, out var location)) return null;
        if (!location.Path.DirectoryExists()) return null;
        if (!locations.IsManaged(path))
        {
            if (_warnedSymlinkedIntrinsics.Add(path))
                Logger.LogWarning("`{Path}` no está entre los archivos gestionados de su ubicación; no se genera", path);
            return null;
        }
        var resolved = locations.ToAbsolutePath(path);
        if (SafePath.IsSymlink(resolved) || SafePath.IsUnderSymlink(location.Path.ToString(), resolved.ToString()))
        {
            if (_warnedSymlinkedIntrinsics.Add(path))
                Logger.LogWarning("`{Path}` es un symlink o está bajo uno; tModManager no lo genera", path);
            return null;
        }
        return factory(path);
    }
```

`using Microsoft.Extensions.DependencyInjection;` ya está en el archivo (usa `GetRequiredService`); `NexusMods.Sdk.IO` (SafePath) también.

- [ ] **Step 3: `Cyberpunk2077Synchronizer` usa la base**

Borrar el campo `_warnedSymlinkedSettings` y reemplazar `IntrinsicFiles` por:

```csharp
    /// <summary>UserSettings.json is generated from the loadout's entries when the prefix location exists.</summary>
    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout)
    {
        var result = base.IntrinsicFiles(loadout);
        var locations = loadout.InstallationInstance.Locations;
        if (!locations.TryGetValue(LocationId.WinePrefix, out var prefix)) return result;
        // The whitelist was computed once when the game was located; use that very path so the two never diverge
        var relative = prefix.ManagedFiles?.FirstOrDefault(f => f.FileName == "UserSettings.json") ?? Cyberpunk2077Game.UserSettingsPath(prefix.Path);
        var path = new GamePath(LocationId.WinePrefix, relative);
        if (DeclareIntrinsic(loadout, path, p => new UserSettingsFile(p)) is { } settings)
            result[path] = settings;
        return result;
    }
```

- [ ] **Step 4: Build 0 warnings y tests de pieza 2/4**

Run: `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | grep -E ' error |Warning\(s\)|Error\(s\)' | grep -v NU19`
Expected: 0/0.

Run: `dotnet test tests/NexusMods.DataModel.Synchronizer.Tests --no-build --filter "FullyQualifiedName~IntrinsicSettingsFileTests|FullyQualifiedName~WinePrefixLocationTests|FullyQualifiedName~LoadoutSettingsTests"`
Expected: todos pasan (en particular `SettingsFileThatIsASymlink_IsNeverWritten`, `SymlinkedSettingsFolder_IsNeverWrittenThroughNorIndexed`, `PrefixDeletedAfterManaging_SyncDoesNotThrow`).

- [ ] **Step 5: Commit**

```bash
git add src/NexusMods.Abstractions.Loadouts.Synchronizers/ALoadoutSynchronizer.cs src/NexusMods.Games.RedEngine/Cyberpunk2077/Cyberpunk2077Synchronizer.cs
git commit -m "feat(sync): IntrinsicFiles declares every ISortOrderFile of the game through DeclareIntrinsic"
```

---

### Task 4: REDmod — la variedad es un `ISortOrderFile`, la whitelist lo incluye, el deploy ya no lo escribe

**Files:**
- Modify: `src/NexusMods.Games.RedEngine/Constants.cs`
- Modify: `src/NexusMods.Games.RedEngine/Cyberpunk2077/SortOrder/RedMod/RedModSortOrderVariety.cs` (clase + `GetRedModOrder` al final)
- Modify: `src/NexusMods.Games.RedEngine/Cyberpunk2077/Cyberpunk2077Game.cs:98-104` (`GetManagedFiles`)
- Modify: `src/NexusMods.Games.RedEngine/Services.cs:21`
- Modify: `src/NexusMods.Games.RedEngine/RedModDeployTool.cs` (todo `Execute`, borrar `WriteLoadOrderFile`)
- Modify: `tests/Games/NexusMods.Games.RedEngine.Tests/RedModDeployToolTests.cs:217-223` (`WriteLoadoutFile`)

**Interfaces:**
- Consumes: `ISortOrderFile` (Task 1).
- Produces: `Constants.RedModListPath = GamePath(WinePrefix, "drive_c/modlist.txt")`; `RedModSortOrderVariety.Render(loadout, db)`.

- [ ] **Step 1: Adaptar el test existente (falla hasta que exista `Render`)**

En `RedModDeployToolTests.cs` reemplazar `WriteLoadoutFile`:

```csharp
    internal Task<string> WriteLoadoutFile(Loadout.ReadOnly loadout)
    {
        loadout = loadout.Rebase();
        var variety = ServiceProvider.GetRequiredService<RedModSortOrderVariety>();
        return Task.FromResult(variety.Render(loadout, loadout.Db));
    }
```

Y agregar al final de la clase:

```csharp
    [Fact]
    public void TheVarietyIsRegisteredAsASortOrderFile_ForCyberpunk()
    {
        var files = ServiceProvider.GetServices<ISortOrderFile>().ToArray();
        files.Should().ContainSingle(f => f is RedModSortOrderVariety);
        files.Single().GameIds.Should().Contain(Cyberpunk2077Game.GameId);
        files.Single().Path.Should().Be(new GamePath(LocationId.WinePrefix, "drive_c/modlist.txt"));
    }
```

`using NexusMods.Abstractions.Loadouts.Synchronizers;` y `using NexusMods.Sdk.Games;` arriba.

- [ ] **Step 2: Ver que falla**

Run: `dotnet build tests/Games/NexusMods.Games.RedEngine.Tests 2>&1 | grep -E ' error ' | head -3`
Expected: `'RedModSortOrderVariety' does not contain a definition for 'Render'`.

- [ ] **Step 3: `Constants.RedModListPath`**

```csharp
    /// <summary>Where the apply writes the REDmod load order; the deploy reads it as C:\modlist.txt.</summary>
    internal static readonly GamePath RedModListPath = new(LocationId.WinePrefix, "drive_c/modlist.txt");
```

- [ ] **Step 4: La variedad implementa `ISortOrderFile`**

En `RedModSortOrderVariety.cs`: `using NexusMods.Abstractions.Loadouts.Synchronizers;` y `using NexusMods.Sdk.Games;`; en la declaración:

```csharp
public class RedModSortOrderVariety : ASortOrderVariety<
    SortItemKey<string>,
    RedModReactiveSortItem,
    SortItemLoadoutData<SortItemKey<string>>,
    SortItemData<SortItemKey<string>> >, ISortOrderFile
```

Agregar dentro de la clase (junto a `GetRedModOrder`):

```csharp
    public IEnumerable<GameId> GameIds => [Cyberpunk2077Game.GameId];
    public GamePath Path => Constants.RedModListPath;

    /// <summary>REDmod only accepts CRLF; anything else ends in "Non-existant mod selected".</summary>
    public string Render(Loadout.ReadOnly loadout, IDb db)
    {
        // TODO: this currently uses the loadout sort order, change this to use the "Active" sort order,
        // once we support switching between collection and loadout sort orders
        var order = GetRedModOrder(loadout.LoadoutId, Optional<CollectionGroupId>.None, db);
        return order.Count > 0 ? string.Join("\r\n", order) : string.Empty;
    }
```

(`Optional` es `DynamicData.Kernel.Optional`, ya importado.)

- [ ] **Step 5: Registro en DI**

En `Services.cs` después de `.AddSingleton<RedModSortOrderVariety, RedModSortOrderVariety>()`:

```csharp
            .AddSingleton<ISortOrderFile>(provider => provider.GetRequiredService<RedModSortOrderVariety>())
```

con `using NexusMods.Abstractions.Loadouts.Synchronizers;`.

- [ ] **Step 6: Whitelist**

En `Cyberpunk2077Game.GetManagedFiles`:

```csharp
        return ImmutableDictionary<LocationId, ImmutableHashSet<RelativePath>>.Empty.Add(
            LocationId.WinePrefix,
            [UserSettingsPath(linux.WinePrefixDirectoryPath), Constants.RedModListPath.Path]);
```

- [ ] **Step 7: `RedModDeployTool.Execute` sin generar el modlist**

Reemplazar `Execute` y borrar `WriteLoadOrderFile`:

```csharp
    public async Task Execute(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        var exe = loadout.InstallationInstance.Locations.ToAbsolutePath(RedModPath);
        var deployFolder = loadout.InstallationInstance.Locations.ToAbsolutePath(RedModDeployFolder);

        if (loadout.Installation.Store != GameStore.Steam)
        {
            _logger.LogWarning("Skip running redmod, it's only supported for Steam on Linux at the moment");
            return;
        }
        // The apply wrote the load order (RedModSortOrderVariety as ISortOrderFile) before this tool runs.
        // No file means no prefix, or the file is a symlink the app refuses to write: nothing to deploy with.
        if (!loadout.InstallationInstance.Locations.TryGetValue(RedModListPath.LocationId, out _))
        {
            _logger.LogWarning("Sin prefix de Wine no hay {Path}; no se corre el deploy de REDmod", RedModListPath);
            return;
        }
        var modList = loadout.InstallationInstance.Locations.ToAbsolutePath(RedModListPath);
        if (!modList.FileExists)
        {
            _logger.LogWarning("No existe {Path}; no se corre el deploy de REDmod", modList);
            return;
        }

        // RedMod deploys to this folder and freaks out if it doesn't exist, but our synchronizer will
        // delete it if it's empty, so we need to recreate it here.
        if (!deployFolder.DirectoryExists())
            deployFolder.CreateDirectory();

        // Note (halgari): When we change the redmod order, we need to force redmod to re-run. This is due to a bug in redmod
        // not recognizing order as a change in the mod configuration. If that bug ever gets fixed (does an order check) then
        // we can resolve this by removing the -force flag.
        await using var batchPath = await ExtractTemporaryDeployScript();
        var command = Cli.Wrap(batchPath.ToString()).WithArguments(["deploy", "-force", "-modlist=C:\\modlist.txt"], escape: true);
        await _toolRunner.ExecuteAsync(loadout, command, true, cancellationToken);
    }
```

Borrar del constructor y de los campos `RedModSortOrderVariety _redModSortOrderVariety` (ya no se usa); `TemporaryFileManager` se queda para el `.bat`. Borrar `using NexusMods.Games.RedEngine.Cyberpunk2077.SortOrder;` si queda sin uso. `exe` queda solo si `ExtractTemporaryDeployScript`/`GameToolRunner` lo necesitan; si el compilador avisa variable sin uso, borrarla (0 warnings).

- [ ] **Step 8: Build y tests de REDmod verdes, `.verified.` intactos**

Run: `dotnet build -p:TreatWarningsAsErrors=true 2>&1 | grep -E ' error |Warning\(s\)|Error\(s\)' | grep -v NU19`
Expected: 0/0.

Run: `dotnet test tests/Games/NexusMods.Games.RedEngine.Tests --no-build --filter "FullyQualifiedName~RedModDeployToolTests"`
Expected: todos pasan; `git status --short tests/Games/NexusMods.Games.RedEngine.Tests/*.verified.txt` vacío (ningún `.received.` nuevo).

- [ ] **Step 9: Commit**

```bash
git add src/NexusMods.Games.RedEngine tests/Games/NexusMods.Games.RedEngine.Tests/RedModDeployToolTests.cs
git commit -m "feat(redmod): the apply writes drive_c/modlist.txt; the deploy only reads it"
```

---

### Task 5: Tests de integración con el fixture CP2077 + prefix

**Files:**
- Create: `tests/Games/NexusMods.Games.RedEngine.Tests/SortOrderFileTests.cs`

**Interfaces:**
- Consumes: todo lo anterior. Helpers del fixture: `ACyberpunkIsolatedGameTest<T>` con `WithWinePrefix => true`, `GameInstallation`, `LoadoutManager`, `Synchronizer`, `Connection`, `CreateLoadout()`, `RegisterLocalArchive(path)`, `TemporaryFileManager`, `FileSystem`, `InitAndGetSortOrderManager()`, `Refresh(ref loadout)`.

- [ ] **Step 1: Escribir los tests (fallan los que dependen de comportamiento todavía no probado; la mayoría ya pasan tras Task 4 — el valor está en fijarlos)**

```csharp
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.RedEngine.Cyberpunk2077.SortOrder;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;
using Xunit;

namespace NexusMods.Games.RedEngine.Tests;

/// <summary>
/// The REDmod load order is a file the loadout owns: written on apply, pending when the order changes,
/// restored when edited by hand, never written through a symlink, backed up when it was there first.
/// </summary>
public class SortOrderFileTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<SortOrderFileTests>(helper)
{
    protected override bool WithWinePrefix => true;

    private static readonly GamePath ModListPath = new(LocationId.WinePrefix, "drive_c/modlist.txt");
    private AbsolutePath Prefix => GameInstallation.Locations[LocationId.WinePrefix].Path;
    private AbsolutePath ModList => Prefix.Combine("drive_c/modlist.txt");

    private async Task<Loadout.ReadOnly> ManagedLoadout()
    {
        _ = InitAndGetSortOrderManager();
        await LoadoutManager.ManageInstallation(GameInstallation);
        await Synchronizer.ReindexState(GameInstallation);
        return await Synchronizer.Synchronize(await CreateLoadout());
    }

    private async Task<Loadout.ReadOnly> AddRedMods(Loadout.ReadOnly loadout)
    {
        await using var tempDir = TemporaryFileManager.CreateFolder();
        foreach (var file in new[] { "one_mod.7z", "several_red_mods.7z" })
        {
            var sourcePath = FileSystem.GetKnownPath(KnownPath.EntryDirectory).Combine("LibraryArchiveInstallerTests/Resources/" + file);
            var copyPath = tempDir.Path.Combine(file);
            File.Copy(sourcePath.ToString(), copyPath.ToString(), overwrite: true);
            var archive = await RegisterLocalArchive(copyPath);
            await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout);
        }
        // The SortOrderManager reacts to loadout changes asynchronously
        await Task.Delay(TimeSpan.FromSeconds(2));
        return loadout.Rebase();
    }

    private async Task<Loadout.ReadOnly> Apply(Loadout.ReadOnly loadout)
    {
        Refresh(ref loadout);
        return await Synchronizer.Synchronize(loadout);
    }

    private string Rendered(Loadout.ReadOnly loadout)
    {
        loadout = loadout.Rebase();
        return ServiceProvider.GetRequiredService<RedModSortOrderVariety>().Render(loadout, loadout.Db);
    }

    // Same shape as IntrinsicSettingsFileTests.ShouldSync
    private bool NeedsSync(Loadout.ReadOnly loadout)
    {
        Refresh(ref loadout);
        var metadata = GameRegistry.ForceGetMetadata(GameInstallation);
        var sync = (ALoadoutSynchronizer)GameInstallation.GetGame().Synchronizer;
        var disk = sync.GetDiskStateForGame(metadata);
        var previous = ((ILoadoutSynchronizer)sync).GetPreviouslyAppliedDiskState(metadata);
        return sync.ShouldSynchronize(loadout, previous, disk);
    }

    private int ExternalChangesCount(Loadout.ReadOnly loadout)
    {
        loadout = loadout.Rebase();
        return LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id)
            .SelectMany(g => g.AsLoadoutItemGroup().Children)
            .Count();
    }

    [Fact]
    public async Task Apply_WritesTheEnabledRedModsInOrder_WithCrlf()
    {
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);

        loadout = await Apply(loadout);

        ModList.FileExists.Should().BeTrue();
        var text = await ModList.ReadAllTextAsync();
        text.Should().Be(Rendered(loadout));
        text.Should().Contain("\r\n").And.NotContain("\n\n");
        text.Split("\r\n").Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task NoRedMods_NoFileIsCreated()
    {
        var loadout = await ManagedLoadout();

        loadout = await Apply(loadout);

        ModList.FileExists.Should().BeFalse("an empty order is nothing to write");
        NeedsSync(loadout).Should().BeFalse();
    }

    [Fact]
    public async Task DisablingARedMod_RemovesItFromTheFile()
    {
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);
        loadout = await Apply(loadout);
        var before = await ModList.ReadAllTextAsync();
        before.Should().Contain("Driver_Shotguns");

        var variety = ServiceProvider.GetRequiredService<RedModSortOrderVariety>();
        var sortOrderId = variety.GetSortOrderIdFor(loadout.LoadoutId).Value;
        var group = variety.GetSortOrderItems(sortOrderId, Connection.Db).Single(i => i.DisplayName == "Driver_Shotguns").ModGroupId!.Value;
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(group, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        await Task.Delay(TimeSpan.FromSeconds(2));

        NeedsSync(loadout).Should().BeTrue("the order changed, the file on disk is stale");
        loadout = await Apply(loadout);

        (await ModList.ReadAllTextAsync()).Should().NotContain("Driver_Shotguns");
        NeedsSync(loadout).Should().BeFalse();
    }

    [Fact]
    public async Task ReorderingInTheLoadout_IsPending_AndApplyRewrites()
    {
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);
        loadout = await Apply(loadout);
        var before = await ModList.ReadAllTextAsync();
        NeedsSync(loadout).Should().BeFalse();

        var variety = ServiceProvider.GetRequiredService<RedModSortOrderVariety>();
        var sortOrderId = variety.GetSortOrderIdFor(loadout.LoadoutId).Value;
        var first = variety.GetSortOrderItems(sortOrderId, Connection.Db).First();
        await variety.MoveItemDelta(sortOrderId, first.Key, 3);

        NeedsSync(loadout).Should().BeTrue();
        loadout = await Apply(loadout);

        var after = await ModList.ReadAllTextAsync();
        after.Should().NotBe(before);
        after.Should().Be(Rendered(loadout));
        NeedsSync(loadout).Should().BeFalse();
    }

    [Fact]
    public async Task HandEdit_IsOverwrittenWithoutExternalChange()
    {
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);
        loadout = await Apply(loadout);
        var wanted = await ModList.ReadAllTextAsync();

        // Same mods, LF line endings and reversed: REDmod would choke on the LF
        await ModList.WriteAllTextAsync(string.Join("\n", wanted.Split("\r\n").Reverse()));
        NeedsSync(loadout).Should().BeTrue();

        loadout = await Apply(loadout);

        (await ModList.ReadAllTextAsync()).Should().Be(wanted);
        ExternalChangesCount(loadout).Should().Be(0, "the game never writes this file, the loadout simply wins");
    }

    [Fact]
    public async Task PrefixMissing_NoFileNoErrorNoDeploy()
    {
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);
        Prefix.DeleteDirectoryNoFollow();

        var act = () => Apply(loadout);

        await act.Should().NotThrowAsync();
        Prefix.DirectoryExists().Should().BeFalse("the app never recreates the prefix");
    }

    [Fact]
    public async Task PrefixDeletedAfterApply_SyncDoesNotThrow()
    {
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);
        loadout = await Apply(loadout);
        ModList.FileExists.Should().BeTrue();

        // Storage Manager "Borrar prefix de Proton"
        Prefix.DeleteDirectoryNoFollow();
        var act = () => Apply(loadout);

        await act.Should().NotThrowAsync();
        Prefix.DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public async Task SymlinkedModlist_IsNeverWrittenThrough()
    {
        var outside = TemporaryFileManager.CreateFolder().Path.Combine("modlist.txt");
        await outside.WriteAllTextAsync("precious");
        ModList.Parent.CreateDirectory();
        File.CreateSymbolicLink(ModList.ToString(), outside.ToString());
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);

        var act = () => Apply(loadout);

        await act.Should().NotThrowAsync("the intrinsic is simply not declared");
        (await outside.ReadAllTextAsync()).Should().Be("precious");
        new FileInfo(ModList.ToString()).LinkTarget.Should().NotBeNull("the link itself is left alone");
    }

    [Fact]
    public async Task PreexistingModlist_IsBackedUpAndRestoredOnUnManage()
    {
        // Written by another manager before tModManager took over
        ModList.Parent.CreateDirectory();
        await ModList.WriteAllTextAsync("vortex_mod\r\n");
        var loadout = await ManagedLoadout();
        loadout = await AddRedMods(loadout);
        loadout = await Apply(loadout);
        (await ModList.ReadAllTextAsync()).Should().NotBe("vortex_mod\r\n");

        await LoadoutManager.UnManage(GameInstallation);

        (await ModList.ReadAllTextAsync()).Should().Be("vortex_mod\r\n");
    }
}
```

Usings extra que piden `NeedsSync`: `using NexusMods.Abstractions.Games;` (`GetGame`) y `using NexusMods.Abstractions.Loadouts.Synchronizers;`.

- [ ] **Step 2: Correr y leer los fallos**

Run: `dotnet build tests/Games/NexusMods.Games.RedEngine.Tests 2>&1 | grep -E ' error ' | head; dotnet test tests/Games/NexusMods.Games.RedEngine.Tests --no-build --filter "FullyQualifiedName~SortOrderFileTests"`
Expected: compila; pasan todos salvo los que destapen un hueco real. Cada fallo se arregla en el código de producción de las Tasks 2–4 (no se afloja el test), con una línea en el commit diciendo qué destapó.

- [ ] **Step 3: Todos en verde**

Run: `dotnet test tests/Games/NexusMods.Games.RedEngine.Tests --no-build --filter "FullyQualifiedName~SortOrderFileTests"`
Expected: 9 passed.

- [ ] **Step 4: Commit**

```bash
git add tests/Games/NexusMods.Games.RedEngine.Tests/SortOrderFileTests.cs
git commit -m "test(redmod): the load order file through apply, reorder, hand edit, prefix gone, symlink, backup"
```

---

### Task 6: Verificación negativa del symlink y de la whitelist

**Files:**
- Modify (temporal, se revierte): `src/NexusMods.Abstractions.Loadouts.Synchronizers/ALoadoutSynchronizer.cs` (`DeclareIntrinsic`)

- [ ] **Step 1: Sacar la guarda de symlink y correr el test**

Comentar en `DeclareIntrinsic` el bloque `if (SafePath.IsSymlink(resolved) || SafePath.IsUnderSymlink(...)) { ... return null; }` y:

Run: `dotnet build tests/Games/NexusMods.Games.RedEngine.Tests 2>&1 | grep -E ' error '; dotnet test tests/Games/NexusMods.Games.RedEngine.Tests --no-build --filter "FullyQualifiedName~SortOrderFileTests.SymlinkedModlist_IsNeverWrittenThrough"`
Expected: FAIL (o lanza `InvalidOperationException` con "symlink" desde `EnsureDiskChangesStayInside`, que también cuenta: el test pide `NotThrowAsync`). Si pasa igual, la guarda no es la que protege y hay que entender por qué antes de seguir.

- [ ] **Step 2: Restaurar y confirmar**

`git checkout -- src/NexusMods.Abstractions.Loadouts.Synchronizers/ALoadoutSynchronizer.cs`, rebuild, mismo test → PASS. `git status --short` limpio.

- [ ] **Step 3: Anotar el resultado en el commit de la Task 7 (no hay commit acá).**

---

### Task 7: Suite completa, docs y PR

**Files:**
- Modify: `TODO.md` (tabla "Piezas genéricas", fila 5; "Estado y próximos pasos")
- Modify: `CLAUDE.md` (sección "Loadout & Synchronization", párrafo de intrínsecos)
- Modify: `docs/superpowers/specs/2026-10-11-sort-order-file-design.md` (línea "Estado")

- [ ] **Step 1: Suite local sin red ni flakey, un proyecto por vez**

Run: `./dev.sh` opción 4 (o, equivalente, los proyectos tocados: `dotnet test tests/Games/NexusMods.Games.RedEngine.Tests`, `tests/NexusMods.DataModel.Synchronizer.Tests`, `tests/NexusMods.DataModel.Tests`, `tests/NexusMods.UI.Tests` con `--filter "RequiresNetworking!=True&FlakeyTest!=True"`, y `dotnet run --project tests/NexusMods.Sdk.Tests`, `tests/NexusMods.Backend.Tests`).
Expected: 0 fallos; `dotnet build -p:TreatWarningsAsErrors=true` 0 warnings.

- [ ] **Step 2: `TODO.md`**

Fila 5 de la tabla pasa a:

```
| 5 | Load order que se escribe a archivo (hecha 2026-10-11, PR #77; `ISortOrderFile` + `SortOrderIntrinsicFile`, `DeclareIntrinsic` en la base; CP2077: `drive_c/modlist.txt` de REDmod lo escribe el apply, el deploy solo lo lee; spec en `docs/superpowers/specs/2026-10-11-sort-order-file-design.md`) | `modlist` de REDmod | `Priority` de `mods.settings` | Skyrim, orden de patchers KOTOR |
```

En "Próximo", el punto 2 pasa a: `Pieza 6 (requisitos del prefix como datos). Las piezas 2 a 5 están hechas (PRs #73, #74, #75, #77); la 5 queda por probar con el juego real (abajo).` Y en "Para probar en Linux" agregar:

```
- [ ] **Pieza 5 con el juego real:** reordenar REDmods en la UI, aplicar, confirmar `drive_c/modlist.txt` (CRLF, orden de la UI) y que el deploy de REDmod lo use (log de redMod, `r6/cache/modded` regenerado)
```

(Si el número de PR no es #77, usar el real.)

- [ ] **Step 3: `CLAUDE.md`**

En "Loadout & Synchronization", después del párrafo de "Intrinsic settings files", agregar:

```
Sort order files (`ISortOrderFile`, `SortOrderIntrinsicFile`): a variety that writes its order to a file implements `ISortOrderFile` (`GameIds`, `Path`, `Render`) and is registered as such in DI; `ALoadoutSynchronizer.IntrinsicFiles` wraps every one of the game as an intrinsic through `DeclareIntrinsic` (location declared and on disk, managed, not a symlink; warned once). The loadout is the only writer: a hand edit is overwritten on apply, no External Change. CP2077: `RedModSortOrderVariety` → `WinePrefix:drive_c/modlist.txt` (CRLF), in the prefix whitelist; `RedModDeployTool` only runs `deploy -force -modlist=C:\modlist.txt` and skips when the file is not there.
```

- [ ] **Step 4: Spec: estado**

Cambiar `Estado: diseño aprobado, pendiente de implementar en la rama feat/sort-order-file` por `Estado: implementado en la rama feat/sort-order-file (PR #77), pendiente de prueba con el juego real`.

- [ ] **Step 5: Commit, push, PR**

```bash
git add TODO.md CLAUDE.md docs/superpowers/specs/2026-10-11-sort-order-file-design.md
git commit -m "docs: pieza 5 done, pending the real-game test"
git push -u origin feat/sort-order-file
gh pr create --title "feat: load order written to a file (pieza 5), REDmod modlist owned by the loadout" --body "<resumen por sección del spec + verificación: proyectos corridos y conteos, resultado de la verificación negativa del symlink (Task 6)>"
```

No mergear: lo mergea el usuario. La prueba con el juego real (`./dev.sh` opción 11) la hace el usuario; la lista de qué mirar está en `TODO.md` → "Para probar en Linux".
