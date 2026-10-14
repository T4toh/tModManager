# Requisitos del prefix como datos (pieza 6)

Fecha: 2026-10-14 · Estado: diseño aprobado, rama `feat/prefix-requirements-data`

## Objetivo

Que cada juego declare qué necesita su prefix de Wine/Proton (DLL overrides y paquetes de winetricks)
y que el diagnóstico y el panel "Wine prefix" de Mis juegos sean genéricos. Es la pieza 6 de "Piezas
genéricas para el segundo juego" en `TODO.md` (= "Requisitos del prefix" de la fase 2). CP2077 la
prueba con lo que ya pide hoy; Witcher 3 la usará para `dinput8=n,b`.

Éxito: CP2077 se ve y se comporta igual que hoy; un juego sin requisitos no muestra panel ni emite
diagnósticos; la UI ya no nombra a CP para esto.

### Qué hay hoy

- Los requisitos de CP2077 están duplicados: `WinePrefixRequirementsEmitter` (RedEngine) y
  `WinePrefixStatusViewModel` (`App.UI`), los dos con `winmm`/`version` en `n,b` y
  `d3dcompiler_47`/`vcrun2022`.
- `WinePrefixStatusViewModel.AppliesTo(game)` es `game.GameId == Cyberpunk2077Game.GameId`: la UI
  depende de RedEngine para decidir si muestra el panel (`MyGamesViewModel`) y el aviso al borrar el
  prefix (`StorageManagerPageViewModel`, que además lee `RequiredWinetricksPackages` de la VM).
- El panel tiene una fila fija por paquete en `WinePrefixStatusView.axaml` y dos booleanos en la VM
  (`IsD3dCompiler47Installed`, `IsVcRun2022Installed`).
- Las plantillas de diagnóstico (`Diagnostics.RequiredWineDllOverrides` #5 y
  `RequiredWinetricksPackages` #6, source `NexusMods.Games.RedEngine.Cyberpunk2077`) dicen "Modding
  Cyberpunk 2077 on Linux". `WineDiagnosticHelper` (`Games.Generic`) ya arma las instrucciones de
  forma genérica.
- `WineDllOverride` vive en `NexusMods.Sdk`, igual que `IGameData`.

### Qué se decidió (2026-10-14)

- **Alcance: solo lo que CP usa hoy** (DLL overrides + paquetes de winetricks). Avisos libres (DLSS),
  opciones de lanzamiento, archivos de config y pasos tras recrear el prefix quedan para cuando W3 los
  necesite. (Descartado: una lista de avisos de texto libre, especulativa sin un segundo juego.)
- **Los requisitos son dato del juego: propiedad en `IGameData` con default `null`**, como
  `GetManagedFiles`. (Descartado: servicio por `GameId` en DI, plumbing para un solo implementador;
  archivo JSON por juego.)
- **El emitter sigue en la lista `DiagnosticEmitters` de cada juego.** `DataModel` no conoce
  `Games.Generic`; el juego que declara requisitos agrega el emitter genérico.

## Diseño

### Datos (`NexusMods.Sdk`)

```csharp
public sealed record WinePrefixRequirements(
    ImmutableArray<WineDllOverride> DllOverrides,
    ImmutableArray<string> WinetricksPackages);
```

En `IGameData`:

```csharp
/// <summary>What the game's Wine/Proton prefix needs for modding; null when nothing.</summary>
WinePrefixRequirements? WinePrefixRequirements => null;
```

`Cyberpunk2077Game` la sobreescribe con `winmm` y `version` (`Native, BuiltIn`) y `d3dcompiler_47`,
`vcrun2022` (fuente: wiki de redmodding, el link que hoy está en el emitter).

### Diagnóstico (`NexusMods.Games.Generic`)

- `WinePrefixRequirementsEmitter` se mueve de `RedEngine/Cyberpunk2077/Emitters` a `Games.Generic`.
  Lee `loadout.InstallationInstance.Game.WinePrefixRequirements`; si es `null` no emite nada. Con
  requisitos usa `WineDiagnosticHelper` como hoy.
- Las dos plantillas pasan a un `Diagnostics` nuevo de `Games.Generic` (source
  `NexusMods.Games.Generic`, números 1 y 2) con un valor `{Game}` en lugar de "Cyberpunk 2077". En
  RedEngine se borran #5 y #6 (los números no se reusan).
- Cambia el `DiagnosticId`. Los diagnósticos no se persisten, no hay migración.
- `Cyberpunk2077Game.DiagnosticEmitters` sigue teniendo `new WinePrefixRequirementsEmitter()`, ahora
  el de `Games.Generic`.

### UI (`NexusMods.App.UI`)

- `WinePrefixStatusViewModel` toma los requisitos de `installation.Game.WinePrefixRequirements`.
  `AppliesTo(game)` pasa a ser `game.WinePrefixRequirements is not null`; se borran
  `RequiredOverrides` y `RequiredWinetricksPackages`.
- `IsD3dCompiler47Installed`/`IsVcRun2022Installed` se reemplazan por
  `IReadOnlyList<WinetricksPackageStatus> Packages` (record `Name`, `IsInstalled`), en el orden
  declarado. `HasIssues` usa `Packages.Any(p => !p.IsInstalled)`.
- `WinePrefixStatusView.axaml`: las dos filas fijas pasan a un `ItemsControl` con la misma fila
  (ícono + `"<paquete>: Instalado"` / `"<paquete>: Faltante"`). El ícono se elige en el template
  según `IsInstalled`.
- `StorageManagerPageViewModel`: el aviso de reinstalar sale de
  `installation.Game.WinePrefixRequirements?.WinetricksPackages` (sin requisitos, sin aviso).
- `App.UI` deja de importar `NexusMods.Games.RedEngine` en esos dos archivos (otros usos siguen, se
  sacan con el resto de la fase 2).
- Sin cambios: el panel sale solo para la primera instalación con requisitos; textos en español; el
  botón "Instalar" y el comando de protontricks.

## Tests

- `Games.Generic.Tests`: el emitter con un juego stub sin requisitos no emite nada (no toca el prefix).
  Con requisitos, emite según lo que falta en el prefix: necesita un `ILinuxCompatabilityDataProvider`
  falso. Si el fixture pide Linux, el test se corre allá.
- `Games.RedEngine.Tests`: CP2077 declara exactamente los 4 requisitos de hoy.
- Build completo con `-p:TreatWarningsAsErrors=true`. En la Mac solo pasan los proyectos de lógica
  pura; la suite completa (`./dev.sh` opción 4) y el panel se prueban en Linux.

### Prueba en Linux (va a "Para probar en Linux" del TODO)

Mis juegos → panel "Wine prefix" con las mismas filas y estados que antes; con un paquete faltante, el
diagnóstico "Missing Winetricks packages" y el botón "Instalar" igual que hoy; el diálogo de borrar
el prefix sigue nombrando `d3dcompiler_47 y vcrun2022`.

## Fuera de alcance

Avisos libres, opciones de lanzamiento, un panel por instalación, unificar el chequeo de overrides
duplicado entre la VM (instrucciones en español) y `WineDiagnosticHelper` (en inglés).
