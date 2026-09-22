# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

Fork of NexusMods.App focused exclusively on **Cyberpunk 2077** via **Steam on Linux**. Built with C#/.NET 10 and Avalonia UI. Manages mod installation, load order, file conflicts, and game directory synchronization.

The upstream repository was discontinued. This fork removed support for other games (Stardew Valley, BG3, Skyrim/Fallout, M&B Bannerlord), stores (GOG, Epic Games Store, Xbox), and platforms (Windows, macOS). The app is named **tModManager** (app ID `io.github.t4toh.tmodmanager`, constants in `NexusMods.Sdk/ApplicationConstants.cs`). C# namespaces keep the upstream `NexusMods.*` prefix on purpose.

## Build & Run Commands

```bash
dotnet build                           # Build entire solution
dotnet run --project src/NexusMods.App/NexusMods.App.csproj  # Run the app

dotnet test                            # Run all tests
dotnet test --filter "RequiresNetworking!=True&FlakeyTest!=True"  # Skip network/flakey tests (CI default)
dotnet run --project tests/NexusMods.Sdk.Tests   # TUnit projects (Sdk.Tests, Backend.Tests) do not run under `dotnet test` on .NET 10
dotnet test --filter "FullyQualifiedName~SomeTestClass.SomeMethod"  # Run a single test
dotnet test tests/Games/NexusMods.Games.RedEngine.Tests  # Run RedEngine (CP2077) tests

dotnet build -p:UseSystemExtractor=true  # Use system 7z for extraction

./dev.sh                               # Interactive menu (Spanish) with build/test/AppImage options

# AppImage (requires `dotnet tool install --global KuiperZone.PupNet` + FUSE); dev.sh option 10 does the same
cd src/NexusMods.App && pupnet -y -k AppImage -p DefineConstants=INSTALLATION_METHOD_APPIMAGE   # output: Deploy/OUT/
```

CI is `.github/workflows/ci.yaml` (ubuntu): `dotnet build -warnaserror`, then xUnit projects via `dotnet test` and the two TUnit projects (`Sdk.Tests`, `Backend.Tests`) via `dotnet run`. There is no lint/format step; `.globalconfig` analyzer errors (below) plus warnings-as-errors in CI are the gate. A full build currently produces **0 compiler warnings**; keep it that way (only `NU19xx` NuGet audit warnings from transitive packages remain).

Building on macOS: the StrawberryShake code generator ships as a net9 tool, so run `DOTNET_ROLL_FORWARD=Major dotnet build` if only the net10 runtime is installed. Most tests require Linux (`LinuxInterop` asserts `IsLinux`); on macOS only the pure-logic test projects pass. Real test verification happens in CI or on a Linux box.

Test traits used for filtering: `RequiresNetworking`, `FlakeyTest`, `RequiresApiKey`.

## Roadmap & Known Issues

Pending work, technical debt, and known-error status live in `TODO.md`. Update it when finishing or adding roadmap items; the README only links to it.

## Architecture

### Solution Structure (78 projects: 47 src + 30 test + 1 benchmarks)

The solution (`NexusMods.App.sln`) is organized into layers:

- **`NexusMods.App`** — Entry point. Wires up DI, starts Avalonia UI or CLI. PupNet config: `io.github.t4toh.tmodmanager`.
- **`NexusMods.App.UI`** — Avalonia views and ViewModels (MVVM with ReactiveUI/R3).
- **`NexusMods.App.Cli`** — CLI commands using `[Verb]`/`[Option]`/`[Injected]` attributes.
- **`NexusMods.Backend`** — Core services: Linux interop, file extraction, game locators (Steam + manual), `SignatureChecker` (magic bytes).
- **`NexusMods.DataModel`** — MnemonicDB-based persistence, synchronizer service, loadout manager, `StorageAnalyzer` (Deep Clean + storage management).
- **`NexusMods.Library`** — Mod library management (add/remove/install from library). Empty file detection on download.
- **`NexusMods.Collections`** — Nexus Mods collection download and installation. MD5 rescan, free-user download via curl.
- **`NexusMods.Sdk`** — Shared utilities, `WineParser` (Lutris/WINEDLLOVERRIDES), `Md5Value`, settings infrastructure.
- **`NexusMods.Abstractions.*`** — Interfaces and contracts for all subsystems (21 projects).
- **`NexusMods.Games.RedEngine`** — Cyberpunk 2077 implementation (the only supported game). Includes `EssentialMods`, `CyberpunkDeepCleanTool`, diagnostic emitters (core mods, redundant folders, Wine prefix, REDmod, pattern-based dependencies).
- **`NexusMods.Games.FileHashes`** — File hash database for game version detection (Steam only).
- **`NexusMods.Networking.Steam`** — Steam store integration (the only supported store).
- **`NexusMods.Networking.NexusWebApi`** — Nexus Mods API integration + `FirefoxCookieReader` for cookie-based downloads.
- **`NexusMods.Networking.HttpDownloader`** — HTTP download infrastructure.
- **`NexusMods.Games.Generic`**, **`FOMOD`**, **`AdvancedInstaller`** — Generic mod support and guided installer frameworks (shared infrastructure, not game-specific).

### Supported Game & Store

- **Game:** Cyberpunk 2077 (`NexusMods.Games.RedEngine`) — Steam App ID `1091500`
- **Store:** Steam on Linux only. Game locators: `SteamLocator` + `ManuallyAddedLocator`.
- **OS Interop:** `LinuxInterop` only (no Windows/macOS).
- **App ID:** `io.github.t4toh.tmodmanager`
- **Data Directory:** `~/.local/share/tModManager/` (isolated from official app). The pre-rename directory `NexusMods.App.Cyberpunk` is moved here once at startup (`DataModelSettings.MigrateLegacyDataDirectory`), and the old `com.cyberpunk2077.modmanager.desktop` handler is removed when the nxm handler is registered.
- **Downloads:** Shared with official NexusMods.App to avoid re-downloads.

### Fork-Specific Features

These are custom features not present in upstream:

1. **Cookie-based free downloads** (`FirefoxCookieReader.cs`, `NexusApiClient.cs`): Reads Firefox session cookies and invokes `curl` to bypass Cloudflare TLS fingerprinting. Allows free/supporter users to download collection mods without premium API. Firefox only: copies `cookies.sqlite` to a temp file and reads it via P/Invoke to `libsqlite3.so.0`. Calls to `GenerateDownloadUrl` are serialized (semaphore) to avoid Cloudflare challenge pages; the CDN download itself runs in parallel. Chrome/Chromium would need Secret Service decryption (see README).

2. **Deep Clean tool** (`CyberpunkDeepCleanTool.cs`, `StorageAnalyzer.cs`): Moves mod directories to timestamped backup in `CyberpunkBackups/`, cleans old backups, removes mod groups from DB, rescans game folder. Accessed via Storage Manager page.

3. **Storage Manager** (`IStorageAnalyzer`): Exposes `DeleteAllBackedUpFilesAsync`, `RunDeepCleanOnAllLoadoutsAsync`, `DeleteArchivesAsync`, `DeletePhysicalFilesAsync` for granular storage cleanup.

4. **Essential mods diagnostics** (`EssentialMods.cs`, `CoreModsDiagnosticEmitter.cs`): Tracks 7 essential CP2077 mods (Redscript, RED4ext, CET, ArchiveXL, TweakXL, Codeware, Equipment-EX). Diagnostic emitters check for missing mods, redundant folder structures, Wine prefix requirements, and pattern-based dependencies.

5. **MD5 rescan + collection resilience** (`CollectionDownloader.cs`, `NexusMods.Collections`): Scans downloads folder to match existing files by MD5 hash, avoiding re-downloads. If the MD5 does not match (mod updated), falls back to relative-path mapping. Missing collection archives on disk are re-downloaded; missing `.nx` entries (after Deep Clean / GC) are detected across all archive children and re-extracted from the parent archive. Apply works with partial installs. Warns before installing a collection when another is already installed.

6. **Telemetry removal**: Matomo, Mixpanel, and OpenTelemetry completely removed (no stubs remain).

7. **App isolation**: Custom app ID, independent data directory, independent NXM protocol handler. Shared downloads folder.

8. **Wine/Lutris support** (`WineParser.cs`, `WinePrefixRequirementsEmitter.cs`): Parses Lutris YAML configs, detects DLL overrides, reads winetricks.log.

9. **Manual game locator** (`ManuallyAddedLocator.cs`): User can specify game path and Wine prefix manually.

10. **Magic bytes file detection** (`SignatureChecker.cs`): Detects archive types (7z/zip/rar) by file headers when server doesn't provide extension.

### Dependency Injection Pattern

Every subsystem registers services through static extension methods in its own `Services.cs`:

```csharp
public static IServiceCollection AddRedEngineGames(this IServiceCollection services)
{
    return services
        .AddGame<Cyberpunk2077Game>()
        .AddRedModSortOrderVarietyModel()
        .AddRedModLoadoutGroupModel();
}
```

The main `Services.cs` in `NexusMods.App` chains all of these together. Startup has two modes: `RunAsMain` (full app with UI, database, games) and client mode (minimal services for IPC).

### MnemonicDB Data Models

Data persistence uses MnemonicDB — an immutable, entity-attribute-value database. Models are defined as partial classes implementing `IModelDefinition` with static attribute fields:

```csharp
public partial class LoadoutItem : IModelDefinition
{
    private const string Namespace = "NexusMods.Loadouts.LoadoutItem";
    public static readonly StringAttribute Name = new(Namespace, nameof(Name));
    public static readonly MarkerAttribute Disabled = new(Namespace, nameof(Disabled)) { IsIndexed = true };
    public static readonly ReferenceAttribute<Loadout> Loadout = new(Namespace, nameof(Loadout)) { IsIndexed = true };
}
```

Key concepts:
- **Attribute types**: `StringAttribute`, `MarkerAttribute`, `ReferenceAttribute<T>`, `HashAttribute`, `SizeAttribute`, `GamePathParentAttribute`
- **`[Include<T>]`** on a model inherits all attributes from T (model inheritance)
- **`ReadOnly` partial struct** inside model definitions for typed query results
- **`ITxFunction`** implementations for complex transactional updates (read from `IDb basis`, write to `ITransaction tx`)
- Each model has a generated `.New()` constructor and `FindBy*` static methods

### Game Plugin System

Games implement `IGame` and `IGameData<T>` and register via `AddGame<T>()`. Each game provides:
- `GameId`, `DisplayName`, `NexusModsGameId`
- `StoreIdentifiers` (only `SteamAppIds` in this fork)
- `LibraryItemInstallers` — ordered chain of installers to try
- `DiagnosticEmitters` — health checks and warnings
- `Synchronizer` — game-specific `ILoadoutSynchronizer`
- `GetLocations()` — maps `LocationId` → `AbsolutePath` for game directories

`GamePath` combines a `LocationId` (Game, SaveData, Config, etc.) with a relative path for portable file references. Always use `GamePath` for game file references, never raw absolute paths.

### Loadout & Synchronization

The core mod management loop:
1. **Loadout** — immutable snapshot of all installed mods, their files, and load order
2. **Synchronizer** — three-way diff: previous disk state vs. current game folder vs. desired loadout
3. **Apply** — writes the diff to disk (backs up originals, deploys mod files)
4. **SynchronizerService** — serializes sync operations via semaphore, exposes observable status

Loadout data hierarchy: `Loadout` → `LoadoutItemGroup` (mod) → `LoadoutItem` → `LoadoutFile` (individual file with hash/size/path).

### UI Architecture

Avalonia MVVM with interface-based ViewModels:
- ViewModels inherit `AViewModel<TInterface>` and implement `IXxxViewModel`
- Reactive properties use `[Reactive]` (ReactiveUI.Fody) on auto-properties
- `*DesignViewModel` classes sit alongside real VMs for the Avalonia previewer
- Registration: `AddViewModel<Impl, IInterface>()` + `AddView<View, IInterface>()`, in the subsystem's `Services.cs`
- `IPageFactory` implementations create pages dynamically
- `IViewLocator` resolves Views from ViewModel types
- TreeDataGrid for hierarchical file displays
- R3 observables and DynamicData for reactive collections

### CLI Commands

Defined in `NexusMods.App.Cli` using attributes:
- `[Verb("name", "description")]` on static methods
- `[Option("short", "long", "description")]` for arguments
- `[Injected]` for DI-resolved parameters
- Custom `IOptionParser<T>` implementations for type conversion

## Test Framework

- **xUnit** with `Xunit.DependencyInjection` for constructor-injected services. Each test project has a `Startup.cs` whose `ConfigureServices` calls `AddDefaultServicesForTesting()` (plus `AddLogging(b => b.AddXUnit())`)
- **NSubstitute** for mocking, **FluentAssertions** for assertions, **AutoFixture** for test data
- **Verify** (snapshot testing) with `.verified.` files checked into source; never delete them manually
- **`AGameTest<TGame>`** base class in `NexusMods.Games.TestFramework` provides pre-configured DI with game installations, file stores, loadout managers
- `NexusMods.StandardGameLocators.TestHelpers` stubs game detection for CI environments

## Code Style

- .NET 10, C# with nullable reference types enabled, implicit usings
- UTF-8, LF line endings, 4-space indentation (see `.editorconfig`)
- Centralized NuGet versions in `Directory.Packages.props`; never put `Version=` on a `<PackageReference>`
- Global analyzer rules in `.globalconfig` treated as errors (do not suppress): `CS4014` un-awaited task, `CS8509` non-exhaustive switch, `CA1069` duplicate enum values, `CA2211` visible non-constant static fields, `CA2021` incompatible `Cast`/`OfType`
- Log messages and some UI strings are in Spanish (this is a personal fork)

## What Was Removed (vs upstream)

- **Games:** StardewValley, StardewValley.SMAPI, Larian (BG3), CreationEngine (Skyrim/Fallout), MountAndBlade2Bannerlord
- **Stores:** Networking.GOG, Networking.EpicGameStore, Abstractions.GOG, Abstractions.EpicGameStore
- **Locators:** GOGLocator, EGSLocator, XboxLocator, HeroicGOGLocator, WinePrefixWrappingLocator
- **OS interop:** WindowsInterop, MacOSInterop (only LinuxInterop remains)
- **Telemetry:** Matomo, Mixpanel, OpenTelemetry (completely removed, empty stubs remain)
- **Repo config:** all upstream `.github/` workflows, issue templates, dependabot, `docs/` + mkdocs, submodules (`extern/SMAPI`, `docs/Nexus`), release/signing scripts, codecov/qodana configs, CHANGELOG, CONTRIBUTING
- **FileHashes:** GOG/EGS model definitions, attributes, and data import logic (only Steam remains)
