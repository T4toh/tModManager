# Runner de herramientas Windows en el prefix (pieza 7) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Any `.exe` the apply needs to run (today REDmod deploy) runs in the Wine/Proton environment of *that* installation, Steam or manually added with a Lutris prefix; the embedded `.bat` goes away and the manual "play" stops launching the `.exe` natively on Linux.

**Architecture:** A `WineRuntime` record in `NexusMods.Sdk` (`SteamProton` | `Wine` | `ProtonViaUmu`) hangs off `ILinuxCompatabilityDataProvider.Runtime`, resolved by the locator (Steam: fixed; manual: by reading the Lutris YAML whose `game.prefix` is the installation's prefix, via a new `LutrisConfigReader`). `GameToolRunner` (`Games.Generic`) gets a new signature (installation + exe + args) and builds the command from the runtime; `IWineToolRunner` in `Abstractions.Games` lets `RunGameTool` use it. `RedModDeployTool` runs `redMod.exe` directly through the runner.

**Tech Stack:** C# / .NET 10, CliWrap, YamlDotNet 18.1.0 (already in `Directory.Packages.props`), xUnit v3 + NSubstitute + FluentAssertions (Generic/RedEngine tests), TUnit (`Sdk.Tests`, `Backend.Tests`, run with `dotnet run`), bubblewrap (`sandbox-run.sh`).

**Spec:** `docs/superpowers/specs/2026-10-19-prefix-tool-runner-design.md`

## Global Constraints

- Nothing CP-specific outside `NexusMods.Games.RedEngine`; the runner knows installations, never games.
- `Abstractions.Games` must not reference `Games.Generic` (it is the other way round); that is why `IWineToolRunner` lives in `Abstractions.Games`.
- Lutris only for manual installs: `$XDG_DATA_HOME/lutris/games/*.yml`, runners at `$XDG_DATA_HOME/lutris/runners/wine/<version>/`; default runner when `wine.version` is missing is `ge-proton` (a Proton build, via `umu-run`). No bare prefixes, no Heroic.
- `umu-run` command: `WINEPREFIX=<prefix> PROTONPATH=<proton dir> GAMEID=umu-default umu-run "<exe>" <args>`; binary from `PATH`, else `$XDG_DATA_HOME/lutris/runtime/umu/umu-run`.
- `WINEDLLOVERRIDES` for Lutris runtimes comes from the YAML (`wine.overrides` ∪ `system.env.WINEDLLOVERRIDES`, `wine.overrides` wins per DLL), formatted with `WineDllOverride.ToString()` joined by `;`.
- The runner never validates the exit code (`CommandResultValidation.None`); `RedModDeployTool` throws on non-zero, `RunGameTool` logs a warning.
- REDmod args stay exactly `deploy -force -modlist=C:\modlist.txt` (the last one now derived from `Constants.RedModListPath` through `WinePaths.ToWindowsPath`).
- Log/UI strings in Spanish. Never `DeleteDirectory(recursive: true)` (nothing here deletes directories).
- New NuGet reference only for YamlDotNet in `NexusMods.Sdk`, no `Version=` attribute.
- Build gate: `DOTNET_ROLL_FORWARD=Major dotnet build -p:TreatWarningsAsErrors=true` with 0 warnings. Never `dotnet test` on the whole solution; TUnit projects run with `dotnet run --project`.
- `ImmutableArray`/`ImmutableDictionary` compare by reference: assert on `ToString()`, sequences or indexers, never record equality for `WineRuntime.Wine`/`ProtonViaUmu`.
- Commits: conventional prefix, no `Co-Authored-By` line, no AI attribution (org rule).
- Branch: `feat/prefix-tool-runner` off `main`.

## Review Focus

1. Lutris `game.prefix` with a trailing `/` or different case than the stored prefix still matches (normalized comparison, not `Contains`). Test in Task 2.
2. `wine.version` names a runner folder that exists but has neither `bin/wine` nor `proton`: `Runtime` is `null` with a warning, no exception. Test in Task 2.
3. Paths with spaces (`Cyberpunk 2077`) reach protontricks/wine intact: the exe is quoted by `MakeLaunchCommand`, args use `escape: true`. Test in Task 4 uses a folder with a space.
4. `system: {}` or no `system.env` (the real `steam-*.yml` shape): empty environment, no NRE. Test in Task 2.
5. REDmod exiting non-zero fails the apply (exception), it does not silently pass now that the runner stops validating. Test in Task 5.

---

### Task 1: `WineRuntime`, `Runtime` on the provider, `WinePaths`

**Files:**
- Create: `src/NexusMods.Sdk/Games/Locators/WineRuntime.cs`
- Create: `src/NexusMods.Sdk/WinePaths.cs`
- Modify: `src/NexusMods.Sdk/Games/Locators/ILinuxCompatabilityDataProvider.cs`
- Modify: `src/NexusMods.Backend/Games/Locators/SteamLocator.cs:112-122` (`LinuxCompatibilityDataProvider`)
- Modify: `src/NexusMods.Backend/ManualLinuxCompatabilityDataProvider.cs` (temporary `Runtime => null`, replaced in Task 3)
- Modify: `tests/NexusMods.StandardGameLocators.TestHelpers/StubbedLinuxCompatabilityDataProvider.cs`
- Test: `tests/NexusMods.Sdk.Tests/WinePathsTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace NexusMods.Sdk.Games;
  public abstract record WineRuntime
  {
      public sealed record SteamProton(long AppId) : WineRuntime;
      public sealed record Wine(AbsolutePath RunnerDirectory, ImmutableDictionary<string, string> Environment) : WineRuntime;
      public sealed record ProtonViaUmu(AbsolutePath ProtonDirectory, ImmutableDictionary<string, string> Environment) : WineRuntime;
  }
  // ILinuxCompatabilityDataProvider
  WineRuntime? Runtime { get; }
  // NexusMods.Sdk.WinePaths
  public static string ToWindowsPath(AbsolutePath path, AbsolutePath winePrefix);
  // StubbedLinuxCompatabilityDataProvider
  public WineRuntime? Runtime { get; set; }   // null by default, tests set it
  ```

- [ ] **Step 0: Branch**

```bash
git checkout -b feat/prefix-tool-runner main
```

- [ ] **Step 1: Write the failing tests** (`tests/NexusMods.Sdk.Tests/WinePathsTests.cs`, TUnit)

```csharp
public class WinePathsTests
{
    private static readonly AbsolutePath Prefix = FileSystem.Shared.FromUnsanitizedFullPath("/home/u/.local/share/Steam/steamapps/compatdata/1091500/pfx");

    [Test]
    public async Task FileInDriveCBecomesC()
        => await Assert.That(WinePaths.ToWindowsPath(Prefix.Combine("drive_c/modlist.txt"), Prefix)).IsEqualTo(@"C:\modlist.txt");

    [Test]
    public async Task NestedFileInDriveCKeepsItsFolders()
        => await Assert.That(WinePaths.ToWindowsPath(Prefix.Combine("drive_c/users/steamuser/AppData/Local/x.json"), Prefix)).IsEqualTo(@"C:\users\steamuser\AppData\Local\x.json");

    [Test]
    public async Task FileOutsideThePrefixBecomesZ()
        => await Assert.That(WinePaths.ToWindowsPath(FileSystem.Shared.FromUnsanitizedFullPath("/home/u/Juegos/Cyberpunk 2077/tools/redmod/bin/redMod.exe"), Prefix))
            .IsEqualTo(@"Z:\home\u\Juegos\Cyberpunk 2077\tools\redmod\bin\redMod.exe");

    [Test]
    public async Task FileInThePrefixButOutsideDriveCBecomesZ()
        => await Assert.That(WinePaths.ToWindowsPath(Prefix.Combine("user.reg"), Prefix)).StartsWith(@"Z:\");
}
```

- [ ] **Step 2: Verify it fails**

Run: `DOTNET_ROLL_FORWARD=Major dotnet build tests/NexusMods.Sdk.Tests`
Expected: `CS0103: The name 'WinePaths' does not exist`.

- [ ] **Step 3: Implement**

`WineRuntime.cs` as in Interfaces, XML docs per spec (`SteamProton`: protontricks resolves Proton, SLR and prefix from the app id; `Wine`: `bin/wine` inside `RunnerDirectory`; `ProtonViaUmu`: Proton build outside Steam, launched through `umu-run`).

`ILinuxCompatabilityDataProvider`: add `WineRuntime? Runtime { get; }` with the spec's doc ("null when unknown: no Lutris config, unsupported runner").

`WinePaths.ToWindowsPath`: if `path.InFolder(winePrefix.Combine("drive_c"))` → `"C:\" + path.RelativeTo(drive_c)` with `/` → `\`; else `"Z:" + path.ToString()` with `/` → `\`.

`SteamLocator.LinuxCompatibilityDataProvider`: `public WineRuntime? Runtime { get; } = new WineRuntime.SteamProton(steamGame.AppId.Value);` (set in the constructor).

`ManualLinuxCompatabilityDataProvider`: `public WineRuntime? Runtime => null;` (placeholder, Task 3 replaces it).

`StubbedLinuxCompatabilityDataProvider`: `public WineRuntime? Runtime { get; set; }`.

- [ ] **Step 4: Verify**

Run: `DOTNET_ROLL_FORWARD=Major dotnet run --project tests/NexusMods.Sdk.Tests -- --treenode-filter "/*/*/WinePathsTests/*"`
Expected: 4 passed. Then `DOTNET_ROLL_FORWARD=Major dotnet build -p:TreatWarningsAsErrors=true` → 0 warnings, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/NexusMods.Sdk src/NexusMods.Backend tests/NexusMods.StandardGameLocators.TestHelpers tests/NexusMods.Sdk.Tests
git commit -m "feat(sdk): WineRuntime describes how an installation runs a Windows exe"
```

---

### Task 2: `LutrisConfigReader` (YamlDotNet) and the runtime it resolves

**Files:**
- Create: `src/NexusMods.Sdk/LutrisConfigReader.cs`
- Modify: `src/NexusMods.Sdk/NexusMods.Sdk.csproj` (add `<PackageReference Include="YamlDotNet" />`)
- Modify: `src/NexusMods.Sdk/WineParser.cs:120-243` (Lutris section: delegate to the reader, delete the hand parser)
- Test: `tests/NexusMods.Sdk.Tests/LutrisConfigReaderTests.cs`

**Interfaces:**
- Consumes: `WineRuntime`, `WineDllOverride`, `WineParser.ParseEnvironmentVariable` (Task 1 / existing).
- Produces:
  ```csharp
  namespace NexusMods.Sdk;
  public sealed record LutrisGameConfig(
      string? Prefix, string? Exe, string? WineVersion,
      ImmutableDictionary<string, string> Env,          // system.env, without WINEDLLOVERRIDES
      ImmutableArray<WineDllOverride> Overrides);        // wine.overrides ∪ env WINEDLLOVERRIDES, wine.overrides wins per DLL
  public static class LutrisConfigReader
  {
      public static AbsolutePath DefaultDataDirectory(IFileSystem fs);          // $XDG_DATA_HOME or ~/.local/share, + "lutris"
      public static LutrisGameConfig Parse(string yaml);
      public static LutrisGameConfig? FindByPrefix(AbsolutePath lutrisDataDir, AbsolutePath winePrefix);   // games/*.yml whose Prefix matches
      public static WineRuntime? ResolveRuntime(LutrisGameConfig config, AbsolutePath lutrisDataDir, Action<string>? warn = null);
      public static ImmutableDictionary<string, string> BuildEnvironment(LutrisGameConfig config);   // Env + WINEDLLOVERRIDES from Overrides when non-empty
  }
  ```
  `WineParser.ParseDllOverridesFromLutrisYaml(string)` and `ParseDllOverridesFromLutrisConfigs(string prefixPath)` keep their signatures and become one-liners over the reader. (The spec names this `WineParser.ParseLutrisGameConfig`; it gets its own class so `WineParser` stays a string parser and the reader owns the file system lookups.)

- [ ] **Step 1: Write the failing tests** (`LutrisConfigReaderTests.cs`, TUnit; the three fixtures are inline `const string`s modeled on the real files)

Fixtures:

```yaml
# WineGe: prefix + env + overrides, wine build
game:
  exe: /home/u/Juegos/cp2077-manual/bin/x64/Cyberpunk2077.exe
  prefix: /home/u/Juegos/pfx-cp/
system:
  env:
    UMU_RUNTIME_UPDATE: '0'
    WINEDLLOVERRIDES: version=n,b;winmm=b
  gamemode: false
  prefix_command: ''
wine:
  overrides:
    winmm: n,b
  version: wine-ge-8-26-x86_64
```
```yaml
# GeProton: the real KOTOR shape
game:
  exe: /mnt/Datos/Wine/gog/kotor/drive_c/GOG Games/Star Wars - KotOR/swkotor.exe
  prefix: /mnt/Datos/Wine/gog/kotor
system:
  env:
    MESA_LOADER_DRIVER_OVERRIDE: zink
  prefix_command: ''
wine:
  version: GE-Proton7-55
```
```yaml
# NoPrefixNoVersion: the real vortex-cyberpunk shape
game:
  exe: ''
system:
  env:
    UMU_RUNTIME_UPDATE: '0'
  gamemode: false
  prefix_command: ''
wine:
  version: wine-ge-8-26-x86_64
```
```yaml
# SteamShape: the real steam-*.yml shape (Review Focus 4)
system: {}
null: {}
game:
  appid: '431960'
```

Tests (one `[Test]` each, TUnit asserts):

- `ParsesPrefixExeVersionAndEnv` on `WineGe`: `Prefix == "/home/u/Juegos/pfx-cp/"`, `Exe` ends with `Cyberpunk2077.exe`, `WineVersion == "wine-ge-8-26-x86_64"`, `Env["UMU_RUNTIME_UPDATE"] == "0"`, `Env.ContainsKey("WINEDLLOVERRIDES")` is false.
- `OverridesUnionWithWineOverridesWinning` on `WineGe`: `Overrides.Select(o => o.ToString()).Order()` equals `["version=n,b", "winmm=n,b"]`.
- `BuildEnvironmentAddsWineDllOverrides` on `WineGe`: `BuildEnvironment(cfg)["WINEDLLOVERRIDES"]` is `"winmm=n,b;version=n,b"` or `"version=n,b;winmm=n,b"` (assert split-and-sorted equals `["version=n,b","winmm=n,b"]`).
- `SteamShapeParsesToEmptyConfig` on `SteamShape`: `Prefix` null, `WineVersion` null, `Env.Count == 0`, `Overrides.IsEmpty`.
- `FindByPrefixMatchesWithTrailingSlashAndCase`: temp dir with `games/a.yml` = `WineGe`; `FindByPrefix(tmp, "/home/u/Juegos/pfx-cp")` not null; `FindByPrefix(tmp, "/home/u/Juegos/PFX-CP")` not null (ordinal-ignore-case, trailing separator trimmed); `FindByPrefix(tmp, "/home/u/other")` null.
- `ResolvesWineBuild`: temp dir with `runners/wine/wine-ge-8-26-x86_64/bin/wine` (empty file); `ResolveRuntime(Parse(WineGe), tmp)` is `WineRuntime.Wine` with `RunnerDirectory == tmp/runners/wine/wine-ge-8-26-x86_64` and `Environment["WINEDLLOVERRIDES"]` present.
- `ResolvesProtonBuildViaUmu`: `runners/wine/GE-Proton7-55/proton` (empty file); `ResolveRuntime(Parse(GeProton), tmp)` is `ProtonViaUmu` with `ProtonDirectory == tmp/runners/wine/GE-Proton7-55`, `Environment["MESA_LOADER_DRIVER_OVERRIDE"] == "zink"`.
- `MissingVersionUsesGeProtonDefault`: `runners/wine/ge-proton/proton`; `ResolveRuntime(Parse(NoPrefixNoVersion) with { WineVersion = null }, tmp)` is `ProtonViaUmu(tmp/runners/wine/ge-proton, ...)`.
- `MissingVersionWithoutGeProtonIsNull`: empty temp dir → null, and `warn` received a message.
- `UnknownRunnerFolderIsNull`: version set, folder missing → null, warned.
- `RunnerFolderWithoutWineOrProtonIsNull` (Review Focus 2): `runners/wine/x/` exists, empty → null, warned.

- [ ] **Step 2: Verify it fails**

Run: `DOTNET_ROLL_FORWARD=Major dotnet build tests/NexusMods.Sdk.Tests`
Expected: `CS0103: The name 'LutrisConfigReader' does not exist`.

- [ ] **Step 3: Implement**

`NexusMods.Sdk.csproj`: `<PackageReference Include="YamlDotNet" />`.

`LutrisConfigReader.Parse`: `new DeserializerBuilder().IgnoreUnmatchedProperties().Build()` into a private DTO (`game.prefix`, `game.exe`, `wine.version`, `wine.overrides` as `Dictionary<string,string>?`, `system.env` as `Dictionary<string,string>?`); the `null: {}` key must not break it (unmatched → ignored). Build `Overrides`: parse env `WINEDLLOVERRIDES` with `WineParser.ParseEnvironmentVariable`, then each `wine.overrides` entry as `"<dll>=<types>"` through the same parser, replacing by `DllName` (ordinal-ignore-case). `Env` = `system.env` minus `WINEDLLOVERRIDES`.

`FindByPrefix`: enumerate `lutrisDataDir/games/*.yml` (top level), `Parse` each (skip on exception), match `Prefix` normalized (`TrimEnd('/')`, `OrdinalIgnoreCase`) against `winePrefix.ToString()`.

`ResolveRuntime`: `version = config.WineVersion ?? "ge-proton"`; `dir = lutrisDataDir/runners/wine/<version>`; `dir/bin/wine` file → `Wine(dir, BuildEnvironment(config))`; `dir/proton` file → `ProtonViaUmu(dir, BuildEnvironment(config))`; otherwise `warn?.Invoke(...)` (Spanish, naming the folder) and `null`.

`WineParser`: `ParseDllOverridesFromLutrisYaml(yaml) => LutrisConfigReader.Parse(yaml).Overrides;` `ParseDllOverridesFromLutrisConfigs(prefix) => LutrisConfigReader.FindByPrefix(LutrisConfigReader.DefaultDataDirectory(FileSystem.Shared), FileSystem.Shared.FromUnsanitizedFullPath(prefix))?.Overrides ?? [];` delete `ParseLutrisWineOverridesSection` and the line scanner. Existing callers/tests of these two methods keep compiling.

- [ ] **Step 4: Verify**

Run: `DOTNET_ROLL_FORWARD=Major dotnet run --project tests/NexusMods.Sdk.Tests`
Expected: all passed (including any pre-existing `WineParser` tests). Build gate 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/NexusMods.Sdk tests/NexusMods.Sdk.Tests
git commit -m "feat(sdk): read the Lutris game config and resolve its Wine runtime"
```

---

### Task 3: Manual provider resolves its runtime and unions the overrides

**Files:**
- Modify: `src/NexusMods.Backend/ManualLinuxCompatabilityDataProvider.cs`
- Modify: `src/NexusMods.Backend/Games/Locators/ManuallyAddedLocator.cs:45`
- Test: `tests/NexusMods.Backend.Tests/ManualLinuxCompatabilityDataProviderTests.cs`

**Interfaces:**
- Consumes: `LutrisConfigReader.*`, `WineParser.ParseDllOverridesFromRegistry` (Task 2 / existing).
- Produces: `ManualLinuxCompatabilityDataProvider(AbsolutePath winePrefix, AbsolutePath lutrisDataDir, ILogger logger)`; `Runtime` lazy (first read parses the YAML, result cached, `null` logged once as a warning); `GetWineDllOverrides` = registry ∪ Lutris (Lutris wins per DLL).

- [ ] **Step 1: Write the failing tests** (TUnit, temp folders under `KnownPath.EntryDirectory` like `AJobsTest`)

Setup helper: `tmp/lutris/games/cp.yml` containing `game: {prefix: <tmp/pfx>}`, `wine: {overrides: {winmm: n,b}, version: wine-ge-8-26-x86_64}`; `tmp/lutris/runners/wine/wine-ge-8-26-x86_64/bin/wine` empty file; `tmp/pfx/user.reg` containing

```
[Software\\Wine\\DllOverrides] 1700000000
"msvcp140"="native"
"winmm"="builtin"
```

Tests:
- `RuntimeIsTheLutrisWineBuild`: `provider.Runtime` is `WineRuntime.Wine` whose `RunnerDirectory` ends with `wine-ge-8-26-x86_64` and `Environment["WINEDLLOVERRIDES"] == "winmm=n,b"`.
- `RuntimeIsNullWithoutALutrisConfig`: no `games/*.yml` → `Runtime` null.
- `OverridesUnionRegistryAndLutris_LutrisWins`: `(await provider.GetWineDllOverrides(ct)).Select(o => o.ToString()).Order()` equals `["msvcp140=n", "winmm=n,b"]`.

- [ ] **Step 2: Verify it fails**

Run: `DOTNET_ROLL_FORWARD=Major dotnet build tests/NexusMods.Backend.Tests`
Expected: `CS1729: 'ManualLinuxCompatabilityDataProvider' does not contain a constructor that takes 3 arguments`.

- [ ] **Step 3: Implement**

Constructor gains `AbsolutePath lutrisDataDir`; `ManuallyAddedLocator` passes `LutrisConfigReader.DefaultDataDirectory(_fileSystem)`. `Runtime` = `Lazy<WineRuntime?>`: `FindByPrefix(lutrisDataDir, _winePrefix)` → null → `LogWarning("No hay configuración de Lutris para el prefix {Prefix}; la app no sabe cómo correr ejecutables ahí", ...)` and null; else `ResolveRuntime(config, lutrisDataDir, msg => _logger.LogWarning("{Message}", msg))`. `GetWineDllOverrides`: registry list, then for each Lutris override replace-or-add by `DllName` (ordinal-ignore-case), return as `ImmutableArray`.

- [ ] **Step 4: Verify**

Run: `DOTNET_ROLL_FORWARD=Major dotnet run --project tests/NexusMods.Backend.Tests`
Expected: all passed. Build gate 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/NexusMods.Backend tests/NexusMods.Backend.Tests
git commit -m "feat(backend): a manual install knows its Lutris runtime and all its DLL overrides"
```

---

### Task 4: `IWineToolRunner` and the new `GameToolRunner`

**Files:**
- Create: `src/NexusMods.Abstractions.Games/IWineToolRunner.cs` (interface + `WineRuntimeUnavailableException`)
- Rewrite: `src/NexusMods.Games.Generic/GameToolRunner.cs`
- Modify: `src/NexusMods.Games.Generic/Services.cs` (register `IWineToolRunner` → `GameToolRunner`; `IProtontricksDependency` → `AggregateProtontricksDependency` on Linux)
- Test: `tests/Games/NexusMods.Games.Generic.Tests/GameToolRunnerTests.cs`

**Interfaces:**
- Consumes: `WineRuntime`, `ILinuxCompatabilityDataProvider.Runtime`, `IProtontricksDependency.MakeLaunchCommand(Command, long)`, `IProcessRunner.RunAsync(Command, bool, CancellationToken)`.
- Produces:
  ```csharp
  namespace NexusMods.Abstractions.Games;
  public interface IWineToolRunner
  {
      Task<CommandResult> RunAsync(GameInstallation installation, AbsolutePath executable, IReadOnlyList<string> arguments,
          AbsolutePath? workingDirectory = null, bool logOutput = true, CancellationToken cancellationToken = default);
  }
  public sealed class WineRuntimeUnavailableException(string message) : InvalidOperationException(message);
  // Games.Generic
  public sealed class GameToolRunner(IProcessRunner processRunner, ILogger<GameToolRunner> logger, IProtontricksDependency? protontricks = null) : IWineToolRunner
  ```
  The old `ExecuteAsync(Loadout.ReadOnly, Command, ...)` stays in this task (its only caller is `RedModDeployTool`, so the solution keeps building) and is deleted in Task 5.

- [ ] **Step 1: Write the failing tests** (xUnit, NSubstitute; build installations like `WinePrefixRequirementsEmitterTests.Installation`, with a stub provider whose `Runtime` and `WinePrefixDirectoryPath` are set; exe = a real temp file under a folder named `Juego 2077` for Review Focus 3)

Helper: `(GameToolRunner runner, IProcessRunner process, IProtontricksDependency protontricks) Make()` where `process.RunAsync(Arg.Do<Command>(c => captured = c), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new CommandResult(0, DateTimeOffset.Now, DateTimeOffset.Now))` and `protontricks.MakeLaunchCommand(Arg.Any<Command>(), Arg.Any<long>()).Returns(ci => ValueTask.FromResult(ci.Arg<Command>()))` (pass-through, so the inner command is inspectable).

Tests:
- `SteamProtonGoesThroughProtontricks`: runtime `SteamProton(1091500)`; after `RunAsync(install, exe, ["deploy", "-force"])`: `protontricks.Received().MakeLaunchCommand(Arg.Is<Command>(c => c.TargetFilePath == exe.ToString() && c.WorkingDirPath == exe.Parent.ToString()), 1091500)`; `captured.Arguments` contains `deploy -force`; `captured.Validation == CommandResultValidation.None`.
- `SteamProtonWithoutProtontricksThrows`: runner built with `protontricks: null` → `WineRuntimeUnavailableException`, `process.DidNotReceive().RunAsync(...)`.
- `WineBuildRunsWineWithPrefixAndEnvironment`: runtime `Wine(runnerDir, {FOO=bar, WINEDLLOVERRIDES=winmm=n,b})`: `captured.TargetFilePath == runnerDir/bin/wine`, `captured.Arguments` contains the exe path and `deploy`, `captured.WorkingDirPath == exe.Parent`, `captured.EnvironmentVariables["WINEPREFIX"] == prefix`, `["FOO"] == "bar"`, `["WINEDLLOVERRIDES"] == "winmm=n,b"`.
- `ProtonBuildRunsUmu`: runtime `ProtonViaUmu(protonDir, {})`: `captured.TargetFilePath` ends with `umu-run`; env `PROTONPATH == protonDir`, `GAMEID == "umu-default"`, `WINEPREFIX == prefix`. (Needs `umu-run` on the test machine; it is on the Linux box.)
- `ExplicitWorkingDirectoryWins`: `workingDirectory: other` → `captured.WorkingDirPath == other`.
- `NoRuntimeThrows`: provider `Runtime == null` → `WineRuntimeUnavailableException` whose message mentions `Lutris`; process not called.
- `NoProviderThrows`: `LinuxCompatabilityDataProvider = null` → same exception.
- `MissingExecutableThrowsBeforeRunning`: exe path does not exist → `FileNotFoundException`, process not called.

- [ ] **Step 2: Verify it fails**

Run: `DOTNET_ROLL_FORWARD=Major dotnet build tests/Games/NexusMods.Games.Generic.Tests`
Expected: `CS0246: The type or namespace name 'IWineToolRunner' could not be found`.

- [ ] **Step 3: Implement**

`GameToolRunner.RunAsync`: resolve `provider = installation.LocatorResult.LinuxCompatabilityDataProvider`, `runtime = provider?.Runtime`; `!executable.FileExists` → `FileNotFoundException`; `wd = workingDirectory ?? executable.Parent`; build `Command` by runtime:
- `SteamProton`: `Cli.Wrap(exe).WithArguments(arguments, escape: true).WithWorkingDirectory(wd).WithValidation(None)` → `protontricks.MakeLaunchCommand(cmd, appId)`; `protontricks is null` → `WineRuntimeUnavailableException("protontricks no está instalado; sin él no se puede correr nada en el prefix de Proton de Steam")`.
- `Wine`: `Cli.Wrap(runner/bin/wine).WithArguments([exe, ..arguments], escape: true)`, env = runtime.Environment + `WINEPREFIX`.
- `ProtonViaUmu`: `Cli.Wrap(FindUmuRun())` with `[exe, ..arguments]`, env += `WINEPREFIX`, `PROTONPATH`, `GAMEID=umu-default`. `FindUmuRun()`: each dir in `PATH` containing a file `umu-run`, else `LutrisConfigReader.DefaultDataDirectory(fs)/runtime/umu/umu-run`, else `WineRuntimeUnavailableException("No se encontró umu-run ...")`.
- `null`: `WineRuntimeUnavailableException("Esta instalación no tiene un entorno de Wine conocido (Steam con protontricks, o un prefix configurado en Lutris)")`.
Then `processRunner.RunAsync(command, logOutput, cancellationToken)`. Log at Information the runtime kind and exe before running. Not Linux → `Cli.Wrap(exe)` directly (current behavior).

`Services.cs`: `services.AddSingleton<GameToolRunner>(); services.AddSingleton<IWineToolRunner>(p => p.GetRequiredService<GameToolRunner>());` and inside the Linux block `services.AddSingleton<IProtontricksDependency>(p => p.GetRequiredService<AggregateProtontricksDependency>());`. Comment on the interface: single implementation, forced because `Abstractions.Games` cannot see `Games.Generic`.

- [ ] **Step 4: Verify**

Run: `DOTNET_ROLL_FORWARD=Major dotnet test tests/Games/NexusMods.Games.Generic.Tests --filter "FullyQualifiedName~GameToolRunnerTests"`
Expected: 8 passed. Build gate 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/NexusMods.Abstractions.Games src/NexusMods.Games.Generic tests/Games/NexusMods.Games.Generic.Tests
git commit -m "feat(generic): GameToolRunner runs an exe in the installation's Wine runtime"
```

---

### Task 5: REDmod deploy through the runner, no more `.bat`

**Files:**
- Modify: `src/NexusMods.Games.RedEngine/RedModDeployTool.cs`
- Delete: `src/NexusMods.Games.RedEngine/Resources/Cyberpunk2077/deploy_redmod.bat`, `src/NexusMods.Games.RedEngine/build/NexusMods.Games.RedEngine.targets`
- Modify: `src/NexusMods.Games.RedEngine/NexusMods.Games.RedEngine.csproj` (remove the `<Import Project="build\...">` and the `<None Update="build\...">` item)
- Test: `tests/Games/NexusMods.Games.RedEngine.Tests/RedModDeployToolRunnerTests.cs`

**Interfaces:**
- Consumes: `IWineToolRunner.RunAsync`, `WinePaths.ToWindowsPath`, `Constants.RedModPath` / `RedModListPath` / `RedModDeployFolder`.
- Produces: `RedModDeployTool(IWineToolRunner runner, ILogger<RedModDeployTool> logger)`; `Execute` skips (warning) when `Runtime` is null, throws `InvalidOperationException` when the exit code is non-zero.

- [ ] **Step 1: Write the failing tests** (xUnit, `ACyberpunkIsolatedGameTest<RedModDeployToolRunnerTests>` with `protected override bool WithWinePrefix => true`; the tool is `new RedModDeployTool(runner, NullLogger<RedModDeployTool>.Instance)` with `runner = Substitute.For<IWineToolRunner>()` returning `CommandResult(0, ...)` by default)

Helpers: `Stub(loadout) => (StubbedLinuxCompatabilityDataProvider)loadout.InstallationInstance.LocatorResult.LinuxCompatabilityDataProvider!`; `Prefix(loadout) => Stub(loadout).WinePrefixDirectoryPath`; `WriteModlist(loadout)` creates `Prefix/drive_c/modlist.txt`.

Tests:
- `SkipsWithoutARuntime`: `Stub.Runtime` left null, modlist written → `Execute` returns, `runner.DidNotReceiveWithAnyArgs()`.
- `SkipsWithoutAModlist`: `Stub.Runtime = new WineRuntime.SteamProton(1091500)`, no modlist → runner not called.
- `RunsRedModFromItsFolderWithTheModlist`: runtime set, modlist written → `runner.Received().RunAsync(Arg.Any<GameInstallation>(), Arg.Is<AbsolutePath>(p => p == gameDir/"tools/redmod/bin/redMod.exe"), Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] {"deploy", "-force", @"-modlist=C:\modlist.txt"})), null, true, Arg.Any<CancellationToken>())`; and `gameDir/"r6/cache/modded"` exists afterwards.
- `FailsWhenRedModExitsNonZero` (Review Focus 5): runner returns `CommandResult(1, ...)` → `await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*REDmod*1*")`.

- [ ] **Step 2: Verify it fails**

Run: `DOTNET_ROLL_FORWARD=Major dotnet build tests/Games/NexusMods.Games.RedEngine.Tests`
Expected: `CS1729` on the `RedModDeployTool` constructor.

- [ ] **Step 3: Implement**

`RedModDeployTool`: constructor `(IWineToolRunner runner, ILogger<RedModDeployTool> logger)`; drop `TemporaryFileManager`, `ExtractTemporaryDeployScript`, `System.Reflection`. `Execute`:
1. `provider = loadout.InstallationInstance.LocatorResult.LinuxCompatabilityDataProvider; if (provider?.Runtime is null) { LogWarning("Esta instalación no tiene un entorno de Wine conocido (Steam con protontricks, o un prefix de Lutris); no se corre el deploy de REDmod"); return; }` (replaces the `Store != Steam` guard).
2. Existing `modlist.txt` guards and `deployFolder.CreateDirectory()` unchanged.
3. `exe = Locations.ToAbsolutePath(RedModPath)`; `args = ["deploy", "-force", $"-modlist={WinePaths.ToWindowsPath(modList, provider.WinePrefixDirectoryPath)}"]`; `result = await _runner.RunAsync(loadout.InstallationInstance, exe, args, cancellationToken: cancellationToken)`; `if (result.ExitCode != 0) throw new InvalidOperationException($"El deploy de REDmod terminó con código {result.ExitCode}; revisá Logs/ProcessLogs");`.

Delete the `.bat`, the `.targets`, and the two csproj references. `Cyberpunk2077Synchronizer` and `Services.cs` need no change (`RedModDeployTool` still resolves through DI: `IWineToolRunner` comes from `AddGenericGameSupport`).

- [ ] **Step 4: Verify**

Run: `DOTNET_ROLL_FORWARD=Major dotnet test tests/Games/NexusMods.Games.RedEngine.Tests --filter "FullyQualifiedName~RedModDeployTool"`
Expected: new 4 + existing `RedModDeployToolTests` pass. `grep -rn "deploy_redmod\|STEAM_APP_PATH" src` → nothing. Build gate 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add -A src/NexusMods.Games.RedEngine tests/Games/NexusMods.Games.RedEngine.Tests
git commit -m "refactor(redengine): REDmod deploy runs redMod.exe through the Wine runner, no bat"
```

---

### Task 6: Manual "play" through the runner

**Files:**
- Modify: `src/NexusMods.Abstractions.Games/RunGameTool.cs:56-75` (`Execute`, Linux branch) and the constructor (keep the `IServiceProvider`, add `_wineRunner = serviceProvider.GetService<IWineToolRunner>()`)

**Interfaces:**
- Consumes: `IWineToolRunner.RunAsync`, `ILinuxCompatabilityDataProvider.Runtime`.

- [ ] **Step 1: Implement**

In `Execute`, inside `if (OSInformation.Shared.IsLinux && program.Equals(primaryFile))` after the Steam branch: `if (locatorResult.Store != GameStore.Steam)`: `runtime = locatorResult.LinuxCompatabilityDataProvider?.Runtime`; if `_wineRunner is null || runtime is null` → `LogError("No se puede lanzar {Game}: la instalación no tiene un entorno de Wine conocido (prefix configurado en Lutris)", _game.DisplayName); return;`; else `var result = await _wineRunner.RunAsync(loadout.InstallationInstance, program, commandLineArgs, program.Parent, logOutput: true, cancellationToken); if (result.ExitCode != 0) LogWarning(...)`; then fall through to the existing "wait for processes with the same name" loop (move the `existing = FindMatchingProcesses(...)` snapshot above the branch so the loop still works). The `UseShell`/`RunCommand` path stays for non-Linux.

- [ ] **Step 2: Verify**

Run: `DOTNET_ROLL_FORWARD=Major dotnet build -p:TreatWarningsAsErrors=true`
Expected: 0 warnings, 0 errors. No unit test (process-level behavior); covered by Linux test 3 in the spec.

- [ ] **Step 3: Commit**

```bash
git add src/NexusMods.Abstractions.Games/RunGameTool.cs
git commit -m "feat(games): a manually added game launches through its Wine runtime on Linux"
```

---

### Task 7: `sandbox-run.sh` accepts extra writable paths

**Files:**
- Modify: `sandbox-run.sh` (after `args=(...)`, before `protect "$L"`; header comment)

- [ ] **Step 1: Implement**

Read `SANDBOX_EXTRA_RW` (colon-separated absolute paths). For each non-empty `p`: `mkdir -p "$p"`; `KEEP+=("$p")`; `[[ $p == "$L"/* ]] || args+=(--bind "$p" "$p")` (paths under `~/.local` are already writable through the `$L` bind and only need to be kept out of `protect`). Document in the header: `SANDBOX_EXTRA_RW=/ruta/copia:/ruta/pfx:$HOME/.local/share/umu ./sandbox-run.sh`. `--check` probes `KEEP`, so the extra paths get probed for free.

- [ ] **Step 2: Verify**

```bash
mkdir -p /tmp/claude-1000/sbx-a /tmp/claude-1000/sbx-b
SANDBOX_EXTRA_RW=/tmp/claude-1000/sbx-a:$HOME/.local/share/umu ./sandbox-run.sh --check
```
Expected: lines `rw  /tmp/claude-1000/sbx-a` and `rw  <home>/.local/share/umu`, every `ro` line as before, exit 0. Without the variable the output is unchanged.

- [ ] **Step 3: Commit**

```bash
git add sandbox-run.sh
git commit -m "chore(sandbox): extra writable paths via SANDBOX_EXTRA_RW for the Lutris test"
```

---

### Task 8: Docs, full gate, PR

**Files:**
- Modify: `TODO.md` (row 7 of the "Piezas genéricas" table; item 2 of "Próximo"; "Para probar en Linux")
- Modify: `CLAUDE.md` (Architecture → `NexusMods.Games.Generic` mention; Fork-Specific Features → remove the `.bat` detail if any; "Wine/Lutris support" bullet gains `LutrisConfigReader` + `WineRuntime`)
- Modify: `docs/superpowers/specs/2026-10-19-prefix-tool-runner-design.md` (Estado line → "implementado en la PR #NN, pendiente de prueba con el juego")

- [ ] **Step 1: TODO.md**

Row 7: `Runner de herramientas Windows en el prefix (hecha 2026-10-XX, PR #NN; `WineRuntime` + `IWineToolRunner`/`GameToolRunner`, Lutris vía `LutrisConfigReader`; spec en docs/superpowers/specs/2026-10-19-prefix-tool-runner-design.md)`. "Próximo" item 2: piece 7 done, piece 8 next. "Para probar en Linux": the three real tests from the spec (Steam in the jail; manual with Lutris in the jail with `SANDBOX_EXTRA_RW`, wine-ge then GE-Proton; manual play outside the jail).

- [ ] **Step 2: CLAUDE.md**

One sentence under `NexusMods.Games.Generic`/Game Plugin System: "`IWineToolRunner` (`GameToolRunner`) runs an exe in the installation's `WineRuntime` (`SteamProton` via protontricks, Lutris `Wine`/`ProtonViaUmu`), resolved by the locator; `RedModDeployTool` uses it." Update the `RedModDeployTool` sentence in "Sort order files" if it still mentions the bat.

- [ ] **Step 3: Full gate**

```bash
DOTNET_ROLL_FORWARD=Major dotnet build -p:TreatWarningsAsErrors=true
./dev.sh   # option 4
```
Expected: 0 warnings; every project green (TUnit projects via `dotnet run`).

- [ ] **Step 4: Commit and PR**

```bash
git add TODO.md CLAUDE.md docs/superpowers
git commit -m "docs: piece 7 done, Linux checks for the prefix tool runner"
git push -u origin feat/prefix-tool-runner
gh pr create --title "Runner de herramientas Windows en el prefix (pieza 7)" --body "..."   # no AI attribution footer
```

Then the Linux checks from the spec ("Prueba en Linux" 1–3), each one deleted from TODO.md when it passes.
