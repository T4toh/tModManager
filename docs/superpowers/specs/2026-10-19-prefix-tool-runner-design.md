# Runner de herramientas Windows en el prefix (pieza 7)

Fecha: 2026-10-19 · Estado: implementado en la PR #98 (2026-10-19), tests verdes en Linux; pendiente de prueba con el juego (ver "Para probar en Linux" en TODO.md)

## Objetivo

Que cualquier `.exe` que la app necesite correr "dentro del juego" (hoy, el deploy de REDmod que corre
el apply) se ejecute en el entorno de Wine/Proton de **esa instalación**, sea de Steam o agregada a
mano con un prefix de Lutris. Es la pieza 7 de "Piezas genéricas para el segundo juego" en
`TODO.md`: generaliza `GameToolRunner`. CP2077 la prueba con REDmod; Witcher 3 la usará para
Script Merger y `wcc_lite`; KOTOR para TSLPatcher.

Éxito: el deploy de REDmod sigue andando igual en Steam (dentro de la jaula, `protontricks-launch`
exit 0, `r6/cache/modded` regenerado) y además anda en una instalación manual de CP2077 con un
prefix de Lutris; el `.bat` embebido desaparece; el "play" de una instalación manual deja de
ejecutar el `.exe` pelado en Linux.

### Qué hay hoy

- `GameToolRunner` (`Games.Generic`): un método `ExecuteAsync(loadout, Command)`. En Linux, si la
  tienda es Steam y hay protontricks, reescribe el comando como `protontricks-launch --appid <id>
  "<exe>" <args>` (nativo o flatpak, vía `AggregateProtontricksDependency`); en cualquier otro caso
  corre el comando tal cual, o sea el `.exe` en Linux, que falla. No sabe dónde está el prefix, no
  valida el exe, no toca `WINEDLLOVERRIDES`.
- Único cliente: `RedModDeployTool`. Extrae `deploy_redmod.bat` (embebido desde
  `build/NexusMods.Games.RedEngine.targets`) a un temporal y lo pasa como ejecutable con
  `deploy -force -modlist=C:\modlist.txt`. El `.bat` lee `%STEAM_APP_PATH%` (lo pone
  `protontricks-launch`), hace `cd tools\redmod\bin` y corre `redMod.exe %*`: existía para fijar el
  working dir en versiones de protontricks que lo ignoraban (< 1.12). La ruta `C:\modlist.txt` está
  hardcodeada aunque el archivo se declara como `GamePath(WinePrefix, "drive_c/modlist.txt")`.
- `Cyberpunk2077Synchronizer.Synchronize` llama a `RedModDeployTool.Execute` entre dos pasadas de
  sync. Si la tienda no es Steam, la tool avisa y no corre. Si el deploy falla, el apply falla a
  propósito (ver TODO, "Super Clean nunca corre…").
- Lo que la instalación sabe de su Wine vive en `GameLocatorResult.LinuxCompatabilityDataProvider`
  (`ILinuxCompatabilityDataProvider`, `Sdk`): ruta del prefix, DLL overrides, winetricks.log. Se
  resuelve al localizar el juego. Steam: `SteamLocator.LinuxCompatibilityDataProvider`
  (prefix fijo `compatdata/<appid>/pfx`). Manual: `ManualLinuxCompatabilityDataProvider`
  (`Backend`), que ya busca el YAML de Lutris que menciona ese prefix
  (`WineParser.ParseDllOverridesFromLutrisConfigs`) para leer `wine.overrides`.
- `RunGameTool<T>` (`Abstractions.Games`): Steam → `steam://run/<appid>` y espera al `reaper`;
  manual → corre el exe directo con `IProcessRunner`. En Linux eso no anda. No usa `GameToolRunner`.
- Lutris 0.5.22 en la máquina de pruebas. Sus YAML (`$XDG_DATA_HOME/lutris/games/<slug>-<id>.yml`)
  traen `game.prefix`, `game.exe`, `wine.version` (nombre del runner en
  `$XDG_DATA_HOME/lutris/runners/wine/<version>/`; puede faltar → default de
  `~/.config/lutris/runners/wine.yml`), `system.env` (diccionario). El runner puede ser un build de
  wine (`bin/wine` adentro) o un build de Proton (`proton` en la raíz, p. ej. `GE-Proton7-55`, que
  Lutris lanza vía `umu-run`). Hay un caso real de cada uno en la máquina.
- `Games.Generic` referencia `Abstractions.Games`; `RunGameTool` no puede ver `GameToolRunner`.

### Qué se decidió (2026-10-19)

- **Dos ejes separados.** *Qué* correr (exe, args, working dir) lo sabe la herramienta o el juego.
  *Cómo* correr un exe acá lo sabe la **instalación**, no el juego: dos juegos de Steam se corren
  igual, el mismo juego en Steam y en Lutris se corre distinto. El runner no conoce juegos.
- **El "cómo" se resuelve al localizar el juego**, como ya pasa con el prefix, y queda en la
  instalación. (El usuario lo llamó "precarga obligada".)
- **Alcance: lo que corre el apply** (REDmod) más el play de una instalación manual, que es el
  mismo entorno. Sin botón de "correr un exe" ni herramientas GUI por ahora: no está claro que
  sirvan y ya hay botón de play.
- **Manual = prefix de Lutris, nada más por ahora.** Prefix suelto (elegir wine del sistema o
  `umu-run` a mano) y Heroic/Bottles después: son todos managers de perfiles de Wine, cada uno
  será otro lector que produce el mismo dato.
- **Afuera:** receta de lanzamiento por juego (script extenders de Bethesda, renombrar launcher:
  dato del juego estilo pieza 6, cuando llegue Bethesda), pieza 8 (salida del deploy como archivos
  derivados), GOG/Heroic como locators.
- Para probar la ruta manual no hace falta otra copia del juego: `cp -a --reflink=always` de la
  carpeta de Steam (btrfs, instantáneo, cero espacio) + un prefix nuevo creado con Lutris +
  "agregar a mano" apuntando a esa carpeta y ese prefix.

## Diseño

### Datos (`NexusMods.Sdk`)

Un tipo cerrado que describe cómo esta instalación ejecuta un `.exe`. Es dato puro; interpretarlo es
trabajo del runner.

```csharp
/// <summary>How this installation runs a Windows executable. Null when the app does not know how.</summary>
public abstract record WineRuntime
{
    /// <summary>Steam/Proton: protontricks resolves Proton, the Steam Linux Runtime and the prefix from the app id.</summary>
    public sealed record SteamProton(long AppId) : WineRuntime;

    /// <summary>A Wine build (Lutris runner): <c>bin/wine</c> inside <paramref name="RunnerDirectory"/>.</summary>
    public sealed record Wine(AbsolutePath RunnerDirectory, ImmutableDictionary<string, string> Environment) : WineRuntime;

    /// <summary>A Proton build outside Steam (Lutris with GE-Proton): launched through <c>umu-run</c>.</summary>
    public sealed record ProtonViaUmu(AbsolutePath ProtonDirectory, ImmutableDictionary<string, string> Environment) : WineRuntime;
}
```

En `ILinuxCompatabilityDataProvider`:

```csharp
/// <summary>How to run a Windows executable in this prefix; null when unknown (no Lutris config, unsupported runner).</summary>
WineRuntime? Runtime { get; }
```

- `SteamLocator.LinuxCompatibilityDataProvider`: `new WineRuntime.SteamProton(appId)`.
- `ManualLinuxCompatabilityDataProvider`: lee el YAML de Lutris cuyo `game.prefix` es el prefix de la
  instalación (reusa la búsqueda de `WineParser`; el match pasa a ser por el valor de `game.prefix`
  normalizado, no por `Contains` sobre todo el archivo), toma `wine.version` (o el default del
  runner), resuelve `$XDG_DATA_HOME/lutris/runners/wine/<version>/`: si tiene `bin/wine` →
  `Wine`; si tiene `proton` en la raíz → `ProtonViaUmu`; si no existe la carpeta o no hay YAML →
  `null` y un warning con el motivo. Si el YAML no trae `wine.version` (así deja Lutris 0.5.22 un
  juego con el runner por defecto; `~/.config/lutris/runners/wine.yml` puede no existir) se imita a
  Lutris: `runners/wine/ge-proton/` si existe → `ProtonViaUmu`; si no, `null`. `Environment` =
  `system.env` del YAML más `WINEDLLOVERRIDES` armado desde `wine.overrides` del mismo YAML (es como
  Lutris lo pasa: variable de entorno, no registro). Lectura perezosa y cacheada: el YAML se lee la
  primera vez que alguien pide `Runtime`, no en `Locate`.
- Ajustes de la revisión final (2026-10-19): el nivel intermedio de Lutris (`runners/wine.yml`: versión y env del runner, el juego gana; un `ge-proton` a nivel runner cuenta como "sin definir", como hace Lutris) se mezcla antes de resolver; `wine.version: system` es el `wine` del PATH; los builds se buscan en `runners/wine/`, `runners/proton/` y `Steam/compatibilitytools.d/`; el runner pone `WINEDEBUG=-all` salvo que el YAML lo defina (como Lutris); un Play sin runtime muestra un diálogo en vez de solo loguear.
- Bug colateral que se arregla de paso: `GetWineDllOverrides` del provider manual devuelve el
  registro (`user.reg`) y **solo si está vacío** mira Lutris; con `vcrun2022` el registro nunca
  está vacío (`*msvcp140`, `*concrt140`, …), así que los `winmm`/`version` del YAML se perdían,
  también para el emitter de la pieza 6. Pasa a unir registro + Lutris (Lutris gana en un mismo DLL).
- `WineParser` gana `ParseLutrisGameConfig(yaml)` → record con `Prefix`, `Exe`, `WineVersion`,
  `Env`, `Overrides`, con tests unitarios sobre los tres YAML reales de la máquina (anonimizados).
  Parser: YamlDotNet, ya en `Directory.Packages.props` (los YAML reales traen `exe:` en varias
  líneas; el parseo a mano actual no alcanza).

Helper de rutas, también en `Sdk`:

```csharp
public static class WinePaths
{
    /// <summary>C:\... for a file inside the prefix's drive_c, Z:\... for anything else.</summary>
    public static string ToWindowsPath(AbsolutePath path, AbsolutePath winePrefix);
}
```

### Runner (`NexusMods.Games.Generic`)

`GameToolRunner` conserva el nombre y cambia la firma: ya no recibe un `Command` armado sino el qué,
y arma el cómo con el `Runtime` de la instalación.

```csharp
public Task<CommandResult> RunAsync(
    GameInstallation installation,
    AbsolutePath executable,
    IReadOnlyList<string> arguments,
    AbsolutePath? workingDirectory = null,   // default: the executable's folder
    bool logOutput = true,
    CancellationToken cancellationToken = default);
```

Por `Runtime`:

| Runtime | Comando |
|---|---|
| `SteamProton` | `protontricks-launch --appid <id> "<exe>" <args>` (nativo o flatpak, como hoy vía `AggregateProtontricksDependency`), `WorkingDirectory` = carpeta del exe. protontricks ≥ 1.12 respeta el cwd; antes usaba la carpeta del exe: en los dos casos queda bien y el `.bat` sobra |
| `Wine` | `<runner>/bin/wine "<exe>" <args>` con `WINEPREFIX=<prefix>` y `Environment` del runtime (trae `WINEDLLOVERRIDES`) |
| `ProtonViaUmu` | `umu-run "<exe>" <args>` (del PATH, si no `$XDG_DATA_HOME/lutris/runtime/umu/umu-run`) con `WINEPREFIX=<prefix>`, `PROTONPATH=<proton dir>`, `GAMEID=umu-default` y el mismo `Environment` |
| `null` | lanza `WineRuntimeUnavailableException` con un mensaje en español que dice qué faltó (sin YAML de Lutris para ese prefix / runner no encontrado / protontricks no instalado) |

- Antes de correr valida que `executable.FileExists`; si no, excepción clara (hoy el `.bat` imprimía
  "redMod.exe not found").
- `logOutput` sigue yendo a `IProcessRunner` (stdout/stderr a `Logs/ProcessLogs/`). El `CommandResult`
  vuelve tal cual; el exit code lo juzga quien llama, como hoy.
- Sin serialización: hoy la única llamada ocurre dentro del sync, que ya es serial. Agregar un
  semáforo por instalación cuando una segunda herramienta corra fuera del apply.
- Si no es Linux, corre el exe directo (comportamiento actual; la app es Linux-only pero el runner
  no tiene por qué saberlo).

Para que `RunGameTool` (en `Abstractions.Games`, que no ve `Games.Generic`) pueda usarlo:
`IWineToolRunner` en `Abstractions.Games` con esa única firma, implementado por `GameToolRunner` y
registrado como ambos en `Games.Generic.Services`. Interfaz con un solo implementador, forzada por
la dirección de las referencias; se anota en el código.

### Consumidores

**`RedModDeployTool`** (`RedEngine`):

- Se borra `deploy_redmod.bat`, su extracción a temporal y la sección de
  `build/NexusMods.Games.RedEngine.targets` que lo embebe.
- Exe: `GamePath(Game, "tools/redmod/bin/redMod.exe")` resuelto con `Locations.ToAbsolutePath`.
  Args: `deploy -force -modlist=<WinePaths.ToWindowsPath(modlist, prefix)>` (da `C:\modlist.txt`,
  como hoy, pero derivado del `GamePath` declarado).
- La guarda "solo Steam" pasa a ser `installation.LocatorResult.LinuxCompatabilityDataProvider?.Runtime is null`
  → warning en español y no corre (como hoy con "no Steam": sin entorno no hay deploy posible y el
  apply sigue). Con runtime, un fallo del deploy sigue tirando y fallando el apply, a propósito.
- Las guardas de `modlist.txt` (ubicación declarada, archivo existe) y la creación de `r6/cache/modded`
  quedan igual.

**`RunGameTool<T>`** (`Abstractions.Games`), rama manual en Linux: en vez de `RunCommand` directo,
si hay `IWineToolRunner` en DI y la instalación tiene `Runtime`, corre el exe primario por el
runner con `commandLineArgs`; sin runtime, log de error en español y no lanza nada (hoy lanza el exe
en Linux y falla igual, pero sin explicar). La espera por procesos con el mismo nombre sigue igual.
Steam no cambia (`steam://run`). Nota: un juego de Lutris lanzado así no pasa por los extras de
Lutris (DXVK que elige Lutris, gamemode, `prefix_command`); se acepta para esta pieza, y
`lutris lutris:rungameid/<id>` queda como alternativa si en la prueba real molesta.

### Lo que queda en el código viejo

`IProtontricksDependency.MakeLaunchCommand(Command, appId)` sigue existiendo: el runner lo usa para
la rama Steam. `GameToolRunner.ExecuteAsync(loadout, Command)` se elimina (un solo cliente, migrado).

## Tests

- `Sdk.Tests` (TUnit, `dotnet run`): `WineParser.ParseLutrisGameConfig` sobre tres YAML: wine build
  con `prefix` y `env`, GE-Proton con `prefix`, y uno sin `prefix` ni `wine.version` (el caso
  `vortex-cyberpunk` real). `WinePaths.ToWindowsPath` dentro y fuera de `drive_c`.
- `Backend.Tests` (TUnit): `ManualLinuxCompatabilityDataProvider.Runtime` con un `XDG_DATA_HOME`
  temporal: YAML + carpeta de runner con `bin/wine` → `Wine`; con `proton` → `ProtonViaUmu`; sin YAML
  → `null`; YAML con runner inexistente → `null`.
- `Games.Generic.Tests`: `GameToolRunner` con un `IProcessRunner` sustituto que captura el `Command`:
  por cada `Runtime` se verifica ejecutable, argumentos, `WorkingDirectory` y variables de entorno
  (`WINEPREFIX`, `PROTONPATH`, `GAMEID`, `WINEDLLOVERRIDES`); `Runtime` nulo →
  `WineRuntimeUnavailableException`; exe inexistente → excepción antes de correr nada.
- `Games.RedEngine.Tests`: `RedModDeployTool` con el runner sustituido: sin runtime no corre y
  loguea; con runtime recibe `tools/redmod/bin/redMod.exe`, args `deploy -force -modlist=C:\modlist.txt`
  y working dir `tools/redmod/bin`; sin `modlist.txt` no corre (como hoy).
- Build completo con `-p:TreatWarningsAsErrors=true`, suite con `./dev.sh` opción 4.

### Prueba en Linux (va a "Para probar en Linux" del TODO)

1. **Steam, en la jaula** (`./dev.sh` opción 11): con el REDmod de prueba `tMM_TestRedmod_A` ya
   instalado en Loadout A, `loadout synchronize` despliega con `protontricks-launch` exit 0 y
   regenera `r6/cache/modded` (`mods.json` lista el mod). Sin `.bat` en `Temp/`. Si redMod se queja
   del working dir, la salida de respaldo es `cmd.exe /c` con `cd /d`.
2. **Manual con Lutris, también en la jaula** (decidido 2026-10-19): `sandbox-run.sh` gana una
   variable de entorno con rutas extra escribibles (la copia del juego, el prefix nuevo de Lutris,
   `~/.local/share/umu`, donde umu baja el Steam Linux Runtime la primera vez; `--check` las prueba
   también). `cp -a --reflink=always` de la carpeta del juego a otra ruta del mismo filesystem btrfs
   (otro subvolumen sirve); prefix nuevo con Lutris (runner wine-ge) apuntando a esa copia,
   `vcrun2022` y `d3dcompiler_47` con winetricks; "agregar a mano" con esa carpeta y ese prefix;
   instalar el mismo REDmod vacío; sincronizar: el deploy corre con el wine de Lutris, exit 0,
   `r6/cache/modded` regenerado en la copia. Repetir cambiando el runner a GE-Proton para la rama
   `umu-run`.
3. Play de la instalación manual desde la app: el juego arranca con el wine de Lutris (o explica
   por qué no, si no hay runtime). Hay que correrlo fuera de la jaula (Steam y la GPU quedan afuera).

## Fuera de alcance

Prefix suelto sin Lutris, Heroic/Bottles, elegir el wine a mano, herramientas GUI y un botón para
correr un exe cualquiera, receta de lanzamiento por juego (script extenders), serialización entre
herramientas, pieza 8, `WineDllOverride` escrito al registro del prefix.
