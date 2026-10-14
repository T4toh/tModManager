# Requisitos del prefix como datos (pieza 6) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Each game declares its Wine/Proton prefix requirements (DLL overrides + winetricks packages) as data on `IGameData`; the diagnostic emitter and the "Wine prefix" panel become generic and stop naming Cyberpunk.

**Architecture:** A `WinePrefixRequirements` record in `NexusMods.Sdk` and a default-`null` property on `IGameData` (same pattern as `GetManagedFiles`). `Cyberpunk2077Game` declares today's values. The emitter and its two diagnostic templates move from RedEngine to `NexusMods.Games.Generic`. The UI panel reads the requirements from `installation.Game` and builds one row per package.

**Tech Stack:** C# / .NET 10, Avalonia + ReactiveUI, xUnit v3, NSubstitute, FluentAssertions, the `NexusMods.App.Generators.Diagnostics` source generator.

**Spec:** `docs/superpowers/specs/2026-10-14-prefix-requirements-data-design.md`

## Global Constraints

- Nothing CP-specific outside `NexusMods.Games.RedEngine`; no `GameId ==` checks in core/UI for this feature.
- CP2077 requirements stay exactly: overrides `winmm=n,b`, `version=n,b` (`[Native, BuiltIn]`, that order), packages `d3dcompiler_47`, `vcrun2022`.
- UI strings stay in Spanish and unchanged: `"<paquete>: Instalado"` / `"<paquete>: Faltante"`, the Storage Manager note `"...hay que volver a instalar d3dcompiler_47 y vcrun2022: ..."`.
- New diagnostics source: `NexusMods.Games.Generic`, ids 1 (DLL overrides) and 2 (winetricks packages). RedEngine ids 5 and 6 are deleted, not reused.
- Build gate: `DOTNET_ROLL_FORWARD=Major dotnet build -p:TreatWarningsAsErrors=true` with 0 warnings. Never run `dotnet test` on the whole solution.
- macOS: `NexusMods.Games.Generic.Tests` and `NexusMods.Games.RedEngine.Tests` fail at host startup (Linux-only DI). On the Mac the red step is the compile error and the green step is a clean build; the tests themselves run on Linux (`./dev.sh` option 4).
- `ImmutableArray` compares by reference: never assert on `WinePrefixRequirements` / `WineDllOverride` equality, compare `ToString()` or element sequences.
- Commits: conventional prefix (`feat:`, `refactor:`, `test:`, `docs:`), no `Co-Authored-By` line.

## Review Focus

1. Game with requirements but no `LinuxCompatabilityDataProvider` (manual locator without prefix, non-Linux): no diagnostics, no exception. Test in Task 2.
2. Requirements declared but empty (`[]`, `[]`): no diagnostics; the existing `Aggregate` calls in `WineDiagnosticHelper` / the VM must not run on an empty sequence. Test in Task 2.
3. An override present with the wrong types (`winmm=b`): the DLL-override diagnostic is emitted (not only when missing). Test in Task 2.
4. Everything already satisfied: nothing emitted. Test in Task 2.
5. The game name reaches the diagnostic text (`{Game}` = `DisplayName`), not "Cyberpunk 2077". Test in Task 2.

---

### Task 1: `WinePrefixRequirements` on `IGameData`, declared by CP2077

**Files:**
- Create: `src/NexusMods.Sdk/Games/WinePrefixRequirements.cs`
- Modify: `src/NexusMods.Sdk/Games/IGameData.cs` (after `GetManagedFiles`, ~line 49)
- Modify: `src/NexusMods.Games.RedEngine/Cyberpunk2077/Cyberpunk2077Game.cs` (next to `GetManagedFiles`, ~line 98)
- Test: `tests/Games/NexusMods.Games.RedEngine.Tests/Cyberpunk2077DiagnosticTests.cs`

**Interfaces:**
- Produces: `NexusMods.Sdk.Games.WinePrefixRequirements(ImmutableArray<WineDllOverride> DllOverrides, ImmutableArray<string> WinetricksPackages)`; `WinePrefixRequirements? IGameData.WinePrefixRequirements { get; }` (default `null`); `Cyberpunk2077Game.WinePrefixRequirements` (public, non-null).

- [ ] **Step 1: Write the failing test**

Add to `Cyberpunk2077DiagnosticTests` (it already has `Game` typed as `Cyberpunk2077Game`):

```csharp
    [Fact]
    public void CyberpunkDeclaresItsWinePrefixRequirements()
    {
        var requirements = Game.WinePrefixRequirements;
        requirements.Should().NotBeNull();
        requirements!.DllOverrides.Select(o => o.ToString()).Should().Equal("winmm=n,b", "version=n,b");
        requirements.WinetricksPackages.Should().Equal("d3dcompiler_47", "vcrun2022");
    }
```

- [ ] **Step 2: Verify it fails**

Run: `DOTNET_ROLL_FORWARD=Major dotnet build tests/Games/NexusMods.Games.RedEngine.Tests`
Expected: build error `CS1061: 'Cyberpunk2077Game' does not contain a definition for 'WinePrefixRequirements'`.

- [ ] **Step 3: Implement**

`src/NexusMods.Sdk/Games/WinePrefixRequirements.cs`:

```csharp
using System.Collections.Immutable;
using JetBrains.Annotations;

namespace NexusMods.Sdk.Games;

/// <summary>
/// What a game's Wine/Proton prefix needs for modding: DLL overrides (Steam launch options or Lutris env)
/// and winetricks packages (installed with protontricks).
/// </summary>
[PublicAPI]
public sealed record WinePrefixRequirements(
    ImmutableArray<WineDllOverride> DllOverrides,
    ImmutableArray<string> WinetricksPackages);
```

In `IGameData`, right after `GetManagedFiles`:

```csharp
    /// <summary>
    /// What the game's Wine/Proton prefix needs for modding; null when nothing. Drives the prefix diagnostics
    /// and the "Wine prefix" panel in My Games.
    /// </summary>
    WinePrefixRequirements? WinePrefixRequirements => null;
```

In `Cyberpunk2077Game`, next to `GetManagedFiles` (move the wiki link comment from the old emitter here):

```csharp
    // https://wiki.redmodding.org/cyberpunk-2077-modding/for-mod-users/users-modding-cyberpunk-2077/modding-on-linux
    public WinePrefixRequirements WinePrefixRequirements { get; } = new(
        DllOverrides:
        [
            new WineDllOverride("winmm", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]),
            new WineDllOverride("version", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]),
        ],
        WinetricksPackages: ["d3dcompiler_47", "vcrun2022"]);
```

Add `using NexusMods.Sdk;` / `using NexusMods.Sdk.Games;` to `Cyberpunk2077Game.cs` if missing.

- [ ] **Step 4: Verify it builds**

Run: `DOTNET_ROLL_FORWARD=Major dotnet build tests/Games/NexusMods.Games.RedEngine.Tests -p:TreatWarningsAsErrors=true`
Expected: `Build succeeded`, 0 warnings. (On Linux also: `dotnet test tests/Games/NexusMods.Games.RedEngine.Tests --filter "FullyQualifiedName~CyberpunkDeclaresItsWinePrefixRequirements"` → PASS.)

- [ ] **Step 5: Commit**

```bash
git add src/NexusMods.Sdk/Games/WinePrefixRequirements.cs src/NexusMods.Sdk/Games/IGameData.cs src/NexusMods.Games.RedEngine/Cyberpunk2077/Cyberpunk2077Game.cs tests/Games/NexusMods.Games.RedEngine.Tests/Cyberpunk2077DiagnosticTests.cs
git commit -m "feat: games declare their Wine prefix requirements as data"
```

---

### Task 2: Generic `WinePrefixRequirementsEmitter` in `Games.Generic`

**Files:**
- Create: `src/NexusMods.Games.Generic/WinePrefixDiagnostics.cs`
- Create: `src/NexusMods.Games.Generic/WinePrefixRequirementsEmitter.cs`
- Delete: `src/NexusMods.Games.RedEngine/Cyberpunk2077/Emitters/WinePrefixRequirementsEmitter.cs`
- Modify: `src/NexusMods.Games.RedEngine/Cyberpunk2077/Diagnostics.cs:238-287` (delete `RequiredWineDllOverrides` #5 and `RequiredWinetricksPackages` #6)
- Modify: `src/NexusMods.Games.RedEngine/Cyberpunk2077/Cyberpunk2077Game.cs:71` (emitter now from `NexusMods.Games.Generic`)
- Modify: `src/NexusMods.Games.Generic/NexusMods.Games.Generic.csproj` (`InternalsVisibleTo` for the tests)
- Test: `tests/Games/NexusMods.Games.Generic.Tests/WinePrefixRequirementsEmitterTests.cs`

**Interfaces:**
- Consumes: `WinePrefixRequirements`, `IGameData.WinePrefixRequirements` (Task 1).
- Produces: `NexusMods.Games.Generic.WinePrefixRequirementsEmitter : ILoadoutDiagnosticEmitter` with
  `public static IAsyncEnumerable<Diagnostic> Diagnose(GameInstallation installation, WinePrefixRequirements requirements, CancellationToken cancellationToken)`;
  `WinePrefixDiagnostics.Source = "NexusMods.Games.Generic"`, generated `CreateRequiredWineDllOverrides(string Game, string Instructions)` and `CreateRequiredWinetricksPackages(string Game, string Instructions)`.

- [ ] **Step 1: Write the failing tests**

`tests/Games/NexusMods.Games.Generic.Tests/WinePrefixRequirementsEmitterTests.cs`:

```csharp
using System.Collections.Immutable;
using FluentAssertions;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NSubstitute;
using Xunit;

namespace NexusMods.Games.Generic.Tests;

public class WinePrefixRequirementsEmitterTests
{
    private static readonly WinePrefixRequirements Requirements = new(
        DllOverrides: [new WineDllOverride("winmm", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])],
        WinetricksPackages: ["vcrun2022"]);

    private static readonly DiagnosticId DllOverridesId = new(WinePrefixDiagnostics.Source, number: 1);
    private static readonly DiagnosticId PackagesId = new(WinePrefixDiagnostics.Source, number: 2);

    private static GameInstallation Installation(ILinuxCompatabilityDataProvider? linuxCompat)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Juego de prueba");
        var locatorResult = new GameLocatorResult
        {
            Game = game,
            Path = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory),
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [],
            Locator = Substitute.For<IGameLocator>(),
            LinuxCompatabilityDataProvider = linuxCompat,
        };
        return new GameInstallation(locatorResult, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty));
    }

    private static ILinuxCompatabilityDataProvider Prefix(ImmutableArray<WineDllOverride> overrides, ImmutableHashSet<string> packages)
    {
        var prefix = Substitute.For<ILinuxCompatabilityDataProvider>();
        prefix.GetWineDllOverrides(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(overrides));
        prefix.GetInstalledWinetricksComponents(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(packages));
        return prefix;
    }

    private static async Task<List<Diagnostic>> Run(GameInstallation installation, WinePrefixRequirements requirements)
    {
        var result = new List<Diagnostic>();
        await foreach (var diagnostic in WinePrefixRequirementsEmitter.Diagnose(installation, requirements, CancellationToken.None))
            result.Add(diagnostic);
        return result;
    }

    [Fact]
    public async Task NothingWithoutAPrefix()
    {
        (await Run(Installation(linuxCompat: null), Requirements)).Should().BeEmpty();
    }

    [Fact]
    public async Task NothingWhenTheRequirementsAreEmpty()
    {
        var prefix = Prefix([], []);
        (await Run(Installation(prefix), new WinePrefixRequirements([], []))).Should().BeEmpty();
    }

    [Fact]
    public async Task NothingWhenEverythingIsThere()
    {
        var prefix = Prefix([new WineDllOverride("WINMM", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn])], ["vcrun2022", "corefonts"]);
        (await Run(Installation(prefix), Requirements)).Should().BeEmpty();
    }

    [Fact]
    public async Task BothWhenEverythingIsMissing()
    {
        var diagnostics = await Run(Installation(Prefix([], [])), Requirements);
        diagnostics.Select(d => d.Id).Should().Equal(DllOverridesId, PackagesId);
    }

    [Fact]
    public async Task OverrideWithWrongTypesIsReported()
    {
        var prefix = Prefix([new WineDllOverride("winmm", [WineDllOverrideType.BuiltIn])], ["vcrun2022"]);
        var diagnostics = await Run(Installation(prefix), Requirements);
        diagnostics.Select(d => d.Id).Should().Equal(DllOverridesId);
    }

    [Fact]
    public async Task DiagnosticNamesTheGame()
    {
        var diagnostics = await Run(Installation(Prefix([], [])), Requirements);
        diagnostics.Select(d => d switch
        {
            Diagnostic<WinePrefixDiagnostics.RequiredWineDllOverridesMessageData> o => o.MessageData.Game,
            Diagnostic<WinePrefixDiagnostics.RequiredWinetricksPackagesMessageData> p => p.MessageData.Game,
            _ => null,
        }).Should().Equal("Juego de prueba", "Juego de prueba");
    }
}
```

The generated `*MessageData` structs are `internal` (same pattern as `Cyberpunk2077DiagnosticTests.cs:123`), so this task also adds `InternalsVisibleTo` for the test project (Step 3).

- [ ] **Step 2: Verify it fails**

Run: `DOTNET_ROLL_FORWARD=Major dotnet build tests/Games/NexusMods.Games.Generic.Tests`
Expected: build errors `CS0103: The name 'WinePrefixRequirementsEmitter' does not exist` / `'WinePrefixDiagnostics'`.

- [ ] **Step 3: Implement**

`src/NexusMods.Games.Generic/WinePrefixDiagnostics.cs` (texts are today's RedEngine #5/#6 with `{Game}` instead of "Cyberpunk 2077"):

```csharp
using JetBrains.Annotations;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Generators.Diagnostics;

namespace NexusMods.Games.Generic;

public static partial class WinePrefixDiagnostics
{
    public const string Source = "NexusMods.Games.Generic";

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate RequiredWineDllOverrides = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 1))
        .WithTitle("Incorrect WINE configuration")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("Update the WINE DLL overrides to mod on Linux")
        .WithDetails("""
Modding {Game} on Linux requires you to configure WINE or the game will not launch correctly.

A WINE DLL override is a configuration setting that tells WINE not to use its own built-in version of a specific Windows DLL, but instead to use a native (real) Windows DLL file that you provide.

### How to Resolve
{Instructions}

### Technical Details
Many mods use custom loaders or injectors to hook into the game process that are implemented as wrapper or replacements for system DLLs. These mod-provided DLLs would normally get loaded on Windows when they are found inside the game folder, but on WINE, the default behaviour is different and the internal WINE versions are used by default instead. Configuring WINE DLL OVERRIDEs allows changing that behaviour to instead load the mod provided DLLs.

The current WINE DLL overrides are not set up correctly for modding on Linux.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Game")
            .AddValue<string>("Instructions")
        )
        .Finish();

    [DiagnosticTemplate]
    [UsedImplicitly]
    internal static IDiagnosticTemplate RequiredWinetricksPackages = DiagnosticTemplateBuilder
        .Start()
        .WithId(new DiagnosticId(Source, number: 2))
        .WithTitle("Missing Winetricks packages")
        .WithSeverity(DiagnosticSeverity.Critical)
        .WithSummary("Install required packages to mod on Linux")
        .WithDetails("""
Modding {Game} on Linux requires certain packages to be installed in your WINE prefix.

Winetricks packages are often required for modding because they provide essential components or libraries which allow the app to manage your game correctly.


### How to Resolve
{Instructions}

### Technical Details
The current WINE prefix does not have the required packages installed for modding on Linux.
""")
        .WithMessageData(messageBuilder => messageBuilder
            .AddValue<string>("Game")
            .AddValue<string>("Instructions")
        )
        .Finish();
}
```

`src/NexusMods.Games.Generic/WinePrefixRequirementsEmitter.cs`:

```csharp
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.Generic;

/// <summary>
/// Checks the prefix against the game's <see cref="IGameData.WinePrefixRequirements"/>. Games that declare
/// requirements add it to their <c>DiagnosticEmitters</c>.
/// </summary>
public class WinePrefixRequirementsEmitter : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var installation = loadout.InstallationInstance;
        var requirements = installation.Game.WinePrefixRequirements;
        if (requirements is null) yield break;

        await foreach (var diagnostic in Diagnose(installation, requirements, cancellationToken))
            yield return diagnostic;
    }

    public static async IAsyncEnumerable<Diagnostic> Diagnose(GameInstallation installation, WinePrefixRequirements requirements, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var game = installation.Game.DisplayName;

        if (requirements.DllOverrides.Length > 0)
        {
            var dllOverridesInstructions = await WineDiagnosticHelper.GetWineDllOverridesUpdateInstructions(installation, requirements.DllOverrides.ToArray(), cancellationToken: cancellationToken);
            if (dllOverridesInstructions is not null) yield return WinePrefixDiagnostics.CreateRequiredWineDllOverrides(game, dllOverridesInstructions);
        }

        if (requirements.WinetricksPackages.Length > 0)
        {
            var winetricksInstructions = await WineDiagnosticHelper.GetWinetricksInstructions(installation, requirements.WinetricksPackages.ToImmutableHashSet(), cancellationToken: cancellationToken);
            if (winetricksInstructions is not null) yield return WinePrefixDiagnostics.CreateRequiredWinetricksPackages(game, winetricksInstructions);
        }
    }
}
```

In `src/NexusMods.Games.Generic/NexusMods.Games.Generic.csproj`, before `</Project>`:

```xml
    <ItemGroup>
        <InternalsVisibleTo Include="NexusMods.Games.Generic.Tests" />
    </ItemGroup>
```

Then:
- Delete `src/NexusMods.Games.RedEngine/Cyberpunk2077/Emitters/WinePrefixRequirementsEmitter.cs`.
- In `src/NexusMods.Games.RedEngine/Cyberpunk2077/Diagnostics.cs` delete the `RequiredWineDllOverrides` (number 5) and `RequiredWinetricksPackages` (number 6) templates, with their attributes.
- In `Cyberpunk2077Game.cs` the `new WinePrefixRequirementsEmitter()` line stays; make sure it resolves to `NexusMods.Games.Generic.WinePrefixRequirementsEmitter` (`using NexusMods.Games.Generic;` is likely already there; remove a now-unused `using ...Cyberpunk2077.Emitters;` only if the build says so).

- [ ] **Step 4: Verify it builds**

Run: `DOTNET_ROLL_FORWARD=Major dotnet build tests/Games/NexusMods.Games.Generic.Tests -p:TreatWarningsAsErrors=true && DOTNET_ROLL_FORWARD=Major dotnet build tests/Games/NexusMods.Games.RedEngine.Tests -p:TreatWarningsAsErrors=true`
Expected: both `Build succeeded`, 0 warnings. (On Linux: `dotnet test tests/Games/NexusMods.Games.Generic.Tests --filter "FullyQualifiedName~WinePrefixRequirementsEmitterTests"` → 6 PASS.)

- [ ] **Step 5: Commit**

```bash
git add -A src/NexusMods.Games.Generic src/NexusMods.Games.RedEngine tests/Games/NexusMods.Games.Generic.Tests/WinePrefixRequirementsEmitterTests.cs
git commit -m "refactor: generic Wine prefix requirements emitter driven by game data"
```

---

### Task 3: Generic "Wine prefix" panel and Storage Manager note

**Files:**
- Modify: `src/NexusMods.App.UI/Pages/MyGames/WinePrefix/IWinePrefixStatusViewModel.cs`
- Modify: `src/NexusMods.App.UI/Pages/MyGames/WinePrefix/WinePrefixStatusViewModel.cs`
- Modify: `src/NexusMods.App.UI/Pages/MyGames/WinePrefix/WinePrefixStatusView.axaml` (the two fixed rows `d3dcompiler_47` / `vcrun2022`)
- Modify: `src/NexusMods.App.UI/Pages/MyGames/WinePrefix/WinePrefixStatusView.axaml.cs` (the two subscriptions for those rows)
- Modify: `src/NexusMods.App.UI/Pages/StorageManager/StorageManagerPageViewModel.cs:177-180`

No new test: the VM needs a real `GameInstallation` + Avalonia activation and the panel is checked by hand on Linux (spec, "Prueba en Linux"). The gate is the build.

**Interfaces:**
- Consumes: `IGameData.WinePrefixRequirements` (Task 1).
- Produces: `record WinetricksPackageStatus(string Name, bool IsInstalled)`; `IReadOnlyList<WinetricksPackageStatus> IWinePrefixStatusViewModel.Packages`; `WinePrefixStatusViewModel.AppliesTo(IGameData)` keeps its signature (callers `MyGamesViewModel.cs:425` and `StorageManagerPageViewModel.cs:178` unchanged except the note).

- [ ] **Step 1: Interface**

In `IWinePrefixStatusViewModel.cs` replace

```csharp
    bool IsD3dCompiler47Installed { get; }
    bool IsVcRun2022Installed { get; }
```

with

```csharp
    /// <summary>The game's winetricks packages, in declared order.</summary>
    IReadOnlyList<WinetricksPackageStatus> Packages { get; }
```

and add at the end of the file:

```csharp
public record WinetricksPackageStatus(string Name, bool IsInstalled);
```

- [ ] **Step 2: ViewModel**

In `WinePrefixStatusViewModel.cs`:

- Delete `RequiredOverrides`, `RequiredWinetricksPackages` and `using NexusMods.Games.RedEngine.Cyberpunk2077;`.
- Replace `AppliesTo`:

```csharp
    /// <summary>Only games that declare prefix requirements get the panel.</summary>
    public static bool AppliesTo(IGameData game) => game.WinePrefixRequirements is not null;
```

- Add a field and set it in the constructor (after `_installation = installation;`):

```csharp
    private readonly WinePrefixRequirements _requirements;
```

```csharp
        _requirements = installation.Game.WinePrefixRequirements ?? new WinePrefixRequirements([], []);
```

- Replace the two `[Reactive] bool IsD3dCompiler47Installed/IsVcRun2022Installed` properties with:

```csharp
    [Reactive] public IReadOnlyList<WinetricksPackageStatus> Packages { get; set; } = [];
```

- In `RunChecksAsync` replace

```csharp
        IsD3dCompiler47Installed = installedPackages.Contains("d3dcompiler_47");
        IsVcRun2022Installed = installedPackages.Contains("vcrun2022");

        var missingPackages = RequiredWinetricksPackages.Except(installedPackages);
```

with

```csharp
        Packages = _requirements.WinetricksPackages.Select(p => new WinetricksPackageStatus(p, installedPackages.Contains(p))).ToArray();

        var missingPackages = Packages.Where(p => !p.IsInstalled).Select(p => p.Name).ToArray();
```

  and `if (missingPackages.Count == 0)` becomes `if (missingPackages.Length == 0)`.
- Replace `foreach (var required in RequiredOverrides)` with `foreach (var required in _requirements.DllOverrides)` and `var dllOverridesString = RequiredOverrides` with `var dllOverridesString = _requirements.DllOverrides`.
- Replace the `HasIssues` line with:

```csharp
        HasIssues = !IsProtontricksInstalled || Packages.Any(p => !p.IsInstalled) || !HasCorrectDllOverrides;
```

- [ ] **Step 3: View**

In `WinePrefixStatusView.axaml` replace the `<!-- d3dcompiler_47 -->` and `<!-- vcrun2022 -->` blocks with:

```xml
                <!-- Winetricks packages: one row per package the game declares -->
                <StackPanel x:Name="PackagesPanel" Spacing="12" />
```

In `WinePrefixStatusView.axaml.cs` replace the two `// d3dcompiler_47 status` / `// vcrun2022 status` subscriptions with:

```csharp
            // Winetricks packages status
            this.WhenAnyValue(v => v.ViewModel!.Packages)
                .Subscribe(packages =>
                {
                    PackagesPanel.Children.Clear();
                    foreach (var package in packages)
                    {
                        PackagesPanel.Children.Add(new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Children =
                            {
                                new UnifiedIcon { Size = 16, Value = package.IsInstalled ? CheckIcon : WarningIcon },
                                new TextBlock
                                {
                                    Text = package.IsInstalled ? $"{package.Name}: Instalado" : $"{package.Name}: Faltante",
                                    Theme = this.FindResource("BodyMDNormalTheme") as ControlTheme,
                                    VerticalAlignment = VerticalAlignment.Center,
                                },
                            },
                        });
                    }
                })
                .DisposeWith(d);
```

Add the usings it needs: `Avalonia.Layout` (`Orientation`, `VerticalAlignment`), `Avalonia.Styling` (`ControlTheme`), `Avalonia.Controls` (already there; `FindResource` is in `Avalonia.Controls.ResourceNodeExtensions`).

- [ ] **Step 4: Storage Manager note**

In `StorageManagerPageViewModel.cs` replace

```csharp
            var reinstallNote = WinePrefixStatusViewModel.AppliesTo(installation.Game)
                ? $"\n\nDespués, cuando Steam lo recree, hay que volver a instalar {string.Join(" y ", WinePrefixStatusViewModel.RequiredWinetricksPackages.Order(StringComparer.Ordinal))}: el panel \"Wine prefix\" de Mis juegos lo detecta y los instala con un botón."
                : "";
```

with

```csharp
            var packages = installation.Game.WinePrefixRequirements?.WinetricksPackages ?? [];
            var reinstallNote = packages.Length > 0
                ? $"\n\nDespués, cuando Steam lo recree, hay que volver a instalar {string.Join(" y ", packages.Order(StringComparer.Ordinal))}: el panel \"Wine prefix\" de Mis juegos lo detecta y los instala con un botón."
                : "";
```

Remove `using NexusMods.Games.RedEngine...` from this file only if the build reports it unused (the file has other Cyberpunk uses).

- [ ] **Step 5: Verify it builds**

Run: `grep -rn 'IsD3dCompiler47Installed\|IsVcRun2022Installed\|RequiredWinetricksPackages\|RequiredOverrides' src tests --include='*.cs' --include='*.axaml'`
Expected: no output.

Run: `DOTNET_ROLL_FORWARD=Major dotnet build src/NexusMods.App -p:TreatWarningsAsErrors=true`
Expected: `Build succeeded`, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/NexusMods.App.UI/Pages/MyGames/WinePrefix src/NexusMods.App.UI/Pages/StorageManager/StorageManagerPageViewModel.cs
git commit -m "refactor: Wine prefix panel and prefix-delete note read the game's requirements"
```

---

### Task 4: Full build, docs

**Files:**
- Modify: `TODO.md` (status block lines 3-9; table row 6 ~line 184; "Fase 2" item ~line 162; item "Requisitos de cada juego como datos" ~line 137; "Para probar en Linux" ~line 32)
- Modify: `CLAUDE.md` ("Game Plugin System" list, after the `GetManagedFiles()` bullet)
- Modify: `docs/superpowers/specs/2026-10-14-prefix-requirements-data-design.md` (Estado line)

- [ ] **Step 1: Full build with warnings as errors**

Run: `DOTNET_ROLL_FORWARD=Major dotnet build -p:TreatWarningsAsErrors=true 2>&1 | tail -5`
Expected: `Build succeeded`, `0 Warning(s)` apart from `NU19xx`.

- [ ] **Step 2: TODO.md**

- Status block: "Próximo" item 2 says pieza 6 is done in this branch, pending the Linux test; next pieces 7 (runner) / 8 (archivos derivados).
- Table row 6: `Requisitos del prefix como datos (hecha 2026-10-14, rama feat/prefix-requirements-data; IGameData.WinePrefixRequirements + WinePrefixRequirementsEmitter genérico en Games.Generic; spec en docs/superpowers/specs/2026-10-14-prefix-requirements-data-design.md)`.
- "Fase 2" `Requisitos del prefix` and "Requisitos de cada juego como datos": mark the DLL overrides + winetricks part done; keep open (as text in the same item) launch options, config files, steps after recreating the prefix and free-text warnings (DLSS).
- "Para probar en Linux": add
  `- [ ] **Pieza 6 con el juego real:** panel "Wine prefix" de Mis juegos con las mismas filas y estados que antes (d3dcompiler_47, vcrun2022, overrides); con un paquete faltante, el diagnóstico "Missing Winetricks packages" y el botón "Instalar" igual que antes; el diálogo de borrar el prefix sigue nombrando d3dcompiler_47 y vcrun2022. Correr también WinePrefixRequirementsEmitterTests y CyberpunkDeclaresItsWinePrefixRequirements (no corren en Mac)`

- [ ] **Step 3: CLAUDE.md**

After the `GetManagedFiles()` bullet in "Game Plugin System":

```markdown
- `WinePrefixRequirements` — optional DLL overrides + winetricks packages the prefix needs (CP2077: `winmm`/`version` `n,b`, `d3dcompiler_47`, `vcrun2022`). `WinePrefixRequirementsEmitter` (`Games.Generic`, added to the game's `DiagnosticEmitters`) and the "Wine prefix" panel in My Games read it; a game without it gets neither
```

- [ ] **Step 4: Spec status**

`Estado: diseño aprobado, rama ...` → `Estado: implementado en la rama feat/prefix-requirements-data, pendiente de prueba en Linux`.

- [ ] **Step 5: Commit**

```bash
git add TODO.md CLAUDE.md docs/superpowers/specs/2026-10-14-prefix-requirements-data-design.md
git commit -m "docs: pieza 6 done, pending the Linux test"
```
