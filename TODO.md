# TODO

## 📍 Estado y próximos pasos (2026-10-07)

Todo lo mergeado hasta #55 está probado con el juego real. Última prueba completa: 2026-10-06, colección "Welcome to Night City 2.31a" (283 mods) agregada, bajada, instalada, aplicada y jugada. Sin CI: **la verificación es local** (`./dev.sh` opción 4, un proyecto por vez).

**Próximo:**
1. Bugs chicos que salieron de las pruebas (lista de abajo).
2. Pieza 5 (load order que se escribe a archivo) o pieza 6 (requisitos del prefix como datos) de "Piezas genéricas para el segundo juego". Las piezas 2, 3 y 4 están hechas (PRs #73, #74, #NN); la 4 queda por probar con el juego real. Witcher 3 arranca cuando estén las piezas que necesita.

### Pruebas reales

- **2026-10-10** (rama `feat/local-mods-first-class`, PR #74): pieza 3 en la app con un zip en `~/Downloads`: diálogo prellenado (nombre y versión `1.2.3`), Enter agrega, copia en `tModManager/Downloads` y original intacto; "Editar" habilitado solo con una fila local, la fila se refresca al guardar, "Ver página del mod" abre el navegador; agregar el mismo zip otra vez no crea fila ni copia ("ya estaba en la biblioteca"); instalar y sincronizar sin errores. Backfill sin nada que hacer (no había locales viejos)

- **2026-09-25** (rama `fix/delete-no-follow-symlinks`): asistente completo (Deep Clean, prefix de Proton borrado sin tocar nada afuera, verificación de Steam, reset), juego gestionado de cero, "Welcome to Night City" bajada (283 mods) e instalada, juego lanzado con RED4ext + 5 plugins y REDScript sin errores. En el camino se arreglaron: el handler `nxm://` que los tests reescribían (login roto), descargas sin extensión, carrera en la barra de progreso de la colección, y el health check del prefix que nunca corría para Steam (tras recrear el prefix faltaban `vcrun2022`/`d3dcompiler_47`: `protontricks 1091500 -q vcrun2022 d3dcompiler_47`)
- **2026-10-03** (rama `chore/sandbox-run`, dentro de la jaula): store borrado a mano y reaplicado (con la app cerrada y abierta), colección reinstalada sin el store, Storage Manager (Deep Clean, borrar descargas cancelado, borrar prefix). Después, prefix recreado por Steam y health check de Proton verificado: marca `vcrun2022`/`d3dcompiler_47` sin prefix y con prefix nuevo, desaparece tras `protontricks`. En el camino: Deep Clean no movía carpetas entre subvolúmenes btrfs / discos (`EXDEV`), arreglado con `NoFollowMove.MoveDirectoryNoFollow`. Avisos `Unable to extract` de 3 archivos generados en runtime (`red4ext/config.ini`, `final.redscripts.bk`, `.bin` de address_library) con el store borrado: no vienen de ninguna descarga y se regeneran solos
- **2026-10-04** (PR #53 en desarrollo): lo que salió está en "Errores conocidos" con "visto 2026-10-04"; todo arreglado (el último, "Instalar desde la biblioteca un mod sin descarga", el 2026-10-07)
- **2026-10-06** (#53, #54, #55): agregar la colección fallaba (`collection.json` fuera del store, arreglado en #55); después, 283 `GenerateDownloadUrl` aceptados a la primera (#54), 7 cortes del CDN retomados solos, instalación y apply sin errores, primer sync con la lista de originales de #53. Único aviso nuevo: `Unknown FOMOD instruction type: enableallplugins` (abajo). Sin medir: cuánto tarda reescribir la lista de originales con los datos reales
- **2026-10-07** (`main` con #56..#69, app en Debug): rescan automático antes de "bajar requeridos" (247 y 252 de 284 vinculados en 0,36 s y 0,08 s, solo se bajó lo que faltaba) y cookie por stdin (3 curl a `GenerateDownloadUrl` vistos con `pgrep`, ninguno con `Cookie:` en los argumentos). Reinstalados a mano *Flatlined Exit* (su readme queda fuera de la raíz: "Skipping FlatlinedExit_readme.txt") y *WTNC Config* (abrió la ventana del FOMOD, sin el WARN de `enableallplugins`). Tema cueva recorrido sin nada naranja ni ilegible. #57: el `ERR` y el toast salen, pero el primer toast de la sesión se perdía (arreglado en #70). Con la rama de #70: el primer toast se ve con barra roja y la biblioteca abre sin `same key`. Prefix: borrado desde el Storage Manager dentro de la jaula (nada afuera tocado, comparado contra el snapshot de snapper), recreado por Steam, y "Instalar" del panel (fuera de la jaula) corrió `protontricks 1091500 -q d3dcompiler_47 vcrun2022` en 16 s, exit 0, health check en verde. Sin probar: protontricks de flatpak y el mensaje de error con el juego abierto. Colección reinstalada y aplicada entera (7026 archivos sin cambios pendientes): `..` no rompió ningún mod legítimo. Con la rama de #70, Jugar y Aplicar quedan grises durante toda la instalación de la colección y Jugar hasta aplicar. Suite completa (opción 4) en verde, que cubre los menores de la fase 2 (el preset `.preset` queda en los tests de RedEngine: no se usa ninguno). Salieron dos bugs, arreglados en #70: la biblioteca tiraba `same key` con la clave de la colección y el rescan salteaba descargas de menos de 1 KiB

### Pendiente de las pruebas

- [x] **Pieza 2 en el juego real** (probado 2026-10-10 en la jaula, instalación ya gestionada): al sincronizar, `UserSettings.json` pasó a original y se respaldó (sin External Change); un cambio de idioma hecho en el juego apareció como External Change con respaldo; al borrar ese ítem y aplicar volvió el original byte a byte; `cache/` y `CrashInfo.json` intactos, ninguna línea `WinePrefix` en los borrados/extracciones de la colección. Queda sin probar en real: borrar el prefix desde el Storage Manager, reiniciar la app y sincronizar (cubierto por tests; requiere `protontricks 1091500 -q vcrun2022 d3dcompiler_47` después)
- [x] **El rescan MD5 no es automático** (solo el botón "Rescan downloads"): con el reset se bajó todo de nuevo aunque las descargas estaban. Desde 2026-10-07 corre solo antes de "bajar requeridos/opcionales". No rehashea lo que la biblioteca ya registró (usa su MD5) y lo que no conoce lo hashea una vez por sesión; vincular por nombre ahora exige también el tamaño cuando Nexus lo da
- [x] **Avisar al borrar el prefix de Proton** que después hacen falta `vcrun2022` y `d3dcompiler_47` (2026-10-07): el diálogo del Storage Manager y el checkbox del asistente de limpieza lo dicen, y el panel "Wine prefix" de Mis juegos los instala con un botón "Instalar" (protontricks nativo o flatpak, el que haya; `IProtontricksDependency.MakeInstallCommand`) y muestra el mismo comando con "Copiar". Antes era prosa con backticks sin copiar
- [x] **FOMOD: instrucción `enableallplugins` desconocida** (visto 2026-10-06, WARN de `FomodXmlInstaller` al instalar `WTNC Config` y un mod más de la colección; resuelto 2026-10-07): no era un bug. La librería FOMOD la emite al final de **toda** instalación XML que sale bien (`XmlScriptInstaller.cs` de `Nexus-Mods/fomod-installer`) y significa "activar los plugins `.esp`/`.esm` del mod": load order de Bethesda, nada que hacer en CP2077. Ahora se reconoce sin warning. Hace falta de verdad cuando llegue Skyrim/Fallout 4 (`plugins.txt`, pieza 5)
- [ ] **Instaladores FOMOD que nunca se ven** (anotado 2026-10-07): la ventana existe y está registrada (`NexusMods.Games.FOMOD.UI`, `GuidedInstallerUi`, `AddGuidedInstallerUi`) y `FomodXmlInstaller` es el primero de la cadena de CP2077, pero en una colección las opciones salen del `collection.json` (`PresetGuidedInstaller`): nunca se pregunta nada. Instalado a mano desde la biblioteca sí abre la ventana (probado 2026-10-07 con `WTNC Config`). Falta un "Reconfigurar" para correr de nuevo el instalador con la ventana sobre un mod ya instalado (si es de una colección, el mod sale del grupo de solo lectura y pasa a "My Mods")
- [ ] **Super Clean con varios loadouts:** hoy se niega (guarda del 2026-09-25). Arreglo real: conservar todos los snapshots de una corrida (cada pasada con sync crea uno y `PruneOldBackups` se lleva el primero, el único con archivos no gestionados). El orden de snapshots es por nombre (hora local): un cambio de horario o una carpeta ajena en `Backups/` puede elegir mal

### Para probar en Linux

Mergeado o en PR, compilado en Mac pero sin correr en la PC con el juego (en Mac la app no arranca y casi toda la suite falla por `LinuxInterop`). Borrar cada línea cuando quede probada.

- [ ] **Ícono nuevo en el AppImage:** el de `./dev.sh` opción 10 usa el logo de capas (en la app ya probado 2026-10-07: barra lateral, ventana y barra de tareas)
- [ ] **Metadata genérica en el AppImage:** el de `./dev.sh` opción 10 muestra el resumen nuevo en el lanzador (la bienvenida de la app ya probada 2026-10-07)
- [ ] **Pieza 4 con el juego** (PR #NN, solo tests): `loadout settings set -l <loadout> -k /graphics/advanced/DLSS -v '"Off"'` (o cualquier opción visible en el menú), aplicar, abrir el juego y ver el ajuste; cambiarlo desde el menú del juego, salir, aplicar: aparece en External Changes con el valor del juego; borrar esa entrada y aplicar vuelve al valor fijado; borrar `UserSettings.json`, aplicar, regenerado desde la base

### Cómo probar con datos reales

`./dev.sh` opción 11: compila Release, verifica la jaula, saca snapshot de `/home` y abre la app con todo el disco en solo lectura salvo `tModManager`, el juego y el prefix 1091500 (ver `sandbox-run.sh`). Antes: backup de saves (`steamapps/compatdata/1091500/pfx/drive_c/users/steamuser/Saved Games/CD Projekt Red/Cyberpunk 2077/`) y de `~/.local/share/tModManager/`.

Recorrido que cubrió las pruebas de arriba: suite local (opción 4) → agregar e instalar una colección (anotar cuántos mods reutiliza el rescan MD5) → aplicar y lanzar (Redscript, CET y RED4ext sin errores) → borrar `DataModel/Archives/` con la app cerrada y abierta y volver a aplicar (se reextrae de Descargas, nada se baja de Nexus) → Storage Manager (Deep Clean deja `Downloads/` intacta y crea el backup en `tModManager/Backups/<GameId>/`; "Borrar descargas" y "Borrar prefix" piden confirmación).

Logs: `~/.local/state/tModManager/Logs/nexusmods.app.main.current.log`. Login desde un build de `bin/` necesita `/etc/dotnet/install_location` apuntando a `~/.dotnet` (ya está en esta PC). Si algo crashea solo en Debug con `Assertion failed`, es un `Debug.Assert`: anotar cuál.

## ✅ Completado

- [x] Descarga automatizada de colecciones (sin premium, sin browser)
- [x] Captura de enlaces NXM (protocolo independiente)
- [x] Vista unificada de descargas
- [x] Diagnósticos de mods esenciales (Redscript, RED4ext, CET, ArchiveXL, TweakXL, Codeware, Equipment-EX)
- [x] Deep Clean + Storage Manager
- [x] Remoción total de telemetría
- [x] Rebrand a tModManager (ver "Multi-juego")
- [x] Limpieza de código muerto (directorios vacíos, NuGet huérfanos, tiendas removidas, UI de feedback, ComingSoon, settings muertos, premium gates, páginas de debug bajo `#if DEBUG`)
- [x] Limpieza de tests: eliminación de databases de StardewValley, corrección de migración \_0004 para tolerar juegos no registrados, fix de limpieza de carpetas vacías en el Synchronizer, actualización de snapshots Verify, limpieza de referencias a juegos removidos en test data

## 🔄 Consolidación de Vistas de Descarga

Actualmente hay dos sistemas paralelos de descarga con componentes duplicados:

- [x] **Unificar componentes de descarga:** Extraídos `SizeProgressComponent` y `SpeedComponent` a `SharedProgressComponents.cs`, usados por ambas vistas
- [x] **Progreso de colecciones:** Las descargas de colección ahora muestran barra de progreso real y velocidad, conectándose a `IDownloadsService.ActiveDownloads` por `FileMetadataId`
- [x] **Agregar interface a CollectionDataProvider:** Extraída `ICollectionDataProvider` con registro DI apropiado
- [x] **Remover página de descargas vacía:** La navegación a la página standalone de descargas fue eliminada (estaba vacía). El botón de velocidad en el spine es ahora solo informativo. Descargas activas se ordenan arriba en la vista de colección
- [x] **Controles de descarga en colección:** Botones de pausa/resume/cancel en la columna de acciones, conectados a `IDownloadsService`
- [x] **Auto-reordenamiento:** La lista se re-ordena automáticamente cuando una descarga termina para que la siguiente suba al tope
- [x] **Botón "Ver página del mod":** Abre la página de Nexus Mods del mod directamente desde la vista de colección
- [x] **IsLoading infrastructure:** Agregado `IsLoading` a `APageViewModel` con control `LoadingSection` reutilizable
- [ ] **Refactorizar CollectionDownloadViewModel:** ~900 líneas. Extraer lógica de orquestación a un servicio separado
- [ ] **Unificar settings de paralelismo:** `DownloadSettings.MaxParallelDownloads` solo controla descargas de colección, no las regulares

## 🎨 Mejoras de UI/UX

- [x] **Tema "cueva": grafito + índigo** (2026-10-07): primitivos `PrimitiveGraphite*` e `PrimitiveIndigo*` con los tokens de `la-cueva-de-tatoh` (`projects/componentes/src/styles/_tema.scss`); `BrandNeutral` y `BrandPrimary` apuntan a ellos en vez de zinc y naranja, el foco toma el índigo y el degradado del header de colecciones sigue al SurfaceLow nuevo. Los controles no cambian (usan los brushes por nombre). Danger/Warning/Success siguen igual
- [x] **Ícono nuevo** (2026-10-07, opción "Capas"): dos tarjetas superpuestas (el mod encima del juego) con la «t» de trazo de tWriter, en la paleta cueva. `src/NexusMods.App/icon.svg` + `icon.ico` (16-256, los que usa pupnet), y en la app `Assets/tmodmanager-logo.svg` (botón de inicio de la barra lateral, `IconValues.AppLogo`, en vez del logo de Nexus) y `Assets/tmodmanager.ico` (ícono de la ventana). Rasterizado con Chrome headless: el renderer interno de `magick` ignora los `stroke` y la «t» desaparece

- [ ] **Ícono del juego en la barra lateral → página del juego** (pedido 2026-10-04): tocar el ícono de Cyberpunk tendría que llevar a donde hoy lleva el logo de Nexus, donde se maneja el juego (`MyGamesPageFactory`, `SpineViewModel.NavigateToHome`). Hoy el ícono del juego abre el workspace del loadout (`LoadoutPageFactory`). Decidir qué queda en el logo de arriba y cómo se llega al loadout. Va junto con el ícono genérico
- [ ] **Botón "Limpiar biblioteca"** en Storage Manager, junto a "Borrar descargas" (pedido 2026-10-04): quitar todos los items de la biblioteca, no solo los archivos de `Downloads/`. Reusar `LibraryItemRemover`; definir qué pasa con los mods instalados que vienen de esos items. Con confirmación

- [x] **Botón "Borrar prefix de Proton"** en Storage Manager (`IStorageAnalyzer.DeleteProtonPrefixAsync`), con confirmación; el prefix sale de la instalación de cada juego. Probado 2026-09-25 y 2026-10-03

- [x] **Loading indicators:** Agregado `IsLoading` al `APageViewModel` base con control `LoadingSection` reutilizable
- [ ] **Manejo de errores visible:** Muchos ViewModels tienen `// TODO: handle errors`. Implementar notificación al usuario vía `IWindowNotificationService` en todos los comandos async
- [ ] **Empty states consistentes:** El control `EmptyState` existe pero no todas las páginas lo usan. Auditar y completar: MyGames sin juego, Library vacía, Loadout sin mods
- [ ] **Accesibilidad básica:** Faltan `TabIndex`, estilos `:focus`/`:keyboard`, tooltips en botones icon-only
- [ ] **Strings hardcodeados:** Centralizar textos en español en `Language.resx` (actualmente mezclados inline en ViewModels y AXAML)
- [ ] **DesignViewModels rotos:** `ApplyDiffDesignViewModel` tira `NotImplementedException`; varios otros son stubs mínimos

## 🔮 Features Futuras

- [ ] **Endorse de mods desde la app:** Botón de endorse en la UI por cada mod instalado + endorse masivo para colecciones. La API ya tiene el endpoint (`POST /v1/games/{domain}/mods/{id}/endorse.json`). Los modders se lo merecen y la app oficial nunca lo implementó
- [ ] Soporte multi-browser para cookies (Chrome/Chromium). Ver [implementación](README.md#agregar-soporte-para-otro-browser)
- [ ] Optimización de rescan MD5 para carpetas grandes
- [ ] Tema claro / alto contraste (solo existe `NexusFluentDark`)

## 🧹 Limpieza sistemática (PRs #26, #27, #28 mergeados)

Objetivo: codebase confiable antes de tocar features. Un PR por bloque, build + tests entre cada uno.

- [x] **Borrar proyectos vacíos/huérfanos del sln:** `NexusMods.Cli` (0 .cs), `NexusMods.UI` (0 .cs), `App.Generators.Diagnostics.Sample`, `src/Examples` (ejemplos upstream, nadie los referencia)
- [x] **Warnings a cero:** `CS0105` usings duplicados en `Sdk/Loadouts/Models/Loadout.cs`, `CS0168` en `Sdk/Games/IGameData.cs`, `NU1510` `System.Linq` en `Abstractions.Loadouts.Synchronizers.csproj`, `CS0612 Tracking` en `CollectionCreator.cs`, `CS0618` `GameInstallMetadata.Name` / `ManuallyAddedGame` (obsolete upstream; quitar `[Obsolete]` o migrar)
- [x] **`CS8785` resuelto:** era el analyzer transitivo `Weave` (dependencia de `MnemonicDB.SourceGenerator`), no Fody. Se remueve en `Directory.Build.targets`
- [x] **`ExperimentalSettings`:** quitado `StardewValley` de `SupportedGames`. `EnableCollectionSharing` se mantiene (gatea la UI de compartir colecciones)
- [x] **13 `// TODO: handle errors`:** `GraphQlResult.AssertHasData()` ya no hace `Debug.Assert` (crasheaba builds Debug ante cualquier error de API) y la excepción incluye los errores GraphQL. Los dos sitios de UI usan `TryGetData` y no rompen la vista
- [x] **Actualizar CLAUDE.md:** conteo de proyectos, stubs de telemetría, build en macOS
- [ ] **Bugs runtime reales:** pendiente reproducir en Linux con juego instalado (crashes esporádicos reportados). Hipótesis a verificar: hay 106 `Debug.Assert`/`Debug.Fail` en `src/`; en build Debug (`dotnet run`, `dev.sh`) cualquier assert fallido mata el proceso. El AppImage es Release y no los ejecuta. Si los crashes son corriendo desde `dev.sh`, correr con `-c Release` para descartar
- [x] **Paquetes al día** (2026-09-24, PRs #38, #39, #40): vulnerabilidades NuGet a 0, bumps dentro del major, Microsoft.Extensions 10, Humanizer 3, StrawberryShake 16, tests a xunit v3 / TUnit 1.x / Verify 32, Paths 0.22.5. Retenidos a propósito: TreeDataGrid 11.1.1 (11.2+ es comercial, Avalonia Accelerate), FluentAssertions 7.x (8 es comercial), Verify 32.x (33+ trae SponsorCheck que rompe el build), Fomod 1.2.1 (solo Windows), MnemonicDB 0.28.2 (ver abajo)
- [ ] **Avalonia 12** (+ ReactiveUI 24, SkiaSharp 4, Splat 21): migrar ~259 `[Reactive]` de ReactiveUI.Fody (muerto) a ReactiveUI.SourceGenerators; TreeDataGrid 12 es comercial, así que vendorizar el fuente MIT de 11.1 (`AvaloniaUI/Avalonia.Controls.TreeDataGrid`, archivado) y portarlo. Sin apuro mientras Avalonia 11 reciba parches (11.3.22 el 2026-09-11)

## 📦 Dependencias heredadas de Nexus

Decidido 2026-09-24. Todas son GPL-3.0 como tModManager: se pueden vendorizar (copiar el fuente a `src/`) sin problema legal. Criterio: **congeladas en la última versión que funciona; se vendorizan cuando haya un motivo concreto** (bug, vulnerabilidad en una dependencia nativa, versión de .NET que las rompa), no antes.

- **MnemonicDB** (la base: loadouts, mods, colecciones): repo archivado 2025-11. Quedamos en **0.28.2**, la última que usó la app oficial en producción. **No subir a 0.50+**: es una reescritura de API publicada dos semanas antes de archivar, ninguna app real la usó. Si hace falta tocarla, vendorizar el tag `v0.28.2`. Depende de RocksDB 9.10 y DuckDB (vigilar sus advisories). Largo plazo opcional: reemplazar por SQLite (~286 archivos la usan)
- **NexusMods.Paths** (`AbsolutePath`, `GamePath`, filesystem en memoria para tests): sin commits desde 2025-10. Congelada en **0.22.5**. Ojo: 0.22 trae su propio `ChunkedStream`/`IChunkedStreamSource` (este último en el namespace global); usamos el nuestro de `NexusMods.Sdk.IO`, calificado
- **NexusMods.Hashing.xxHash3**: reemplazable por `XxHash3` de `System.IO.Hashing` (paquete oficial de Microsoft). No se hizo con la eliminación de `.nx`; ahora implica migrar los hashes guardados en la base y los nombres del store (`Archives/<2-hex>/<hash>`)
- [x] **Eliminar `.nx` file store** (2026-09-24, PRs #42, #43, #45; probado con la app el 2026-09-25 y el 2026-10-03): reemplazado por `LooseFileStore` (content-addressed, `Archives/<2-hex>/<hash>`) + GC por barrido (`LiveHashes`). Descargas de primera clase en `tModManager/Downloads` con `LibraryFile.DownloadPath` y reextracción vía `IDownloadReExtractor`. Asistente de limpieza guiada para datos viejos (`.nx`, DB vieja), Deep Clean reforzado, borrado del prefix de Proton. Salen `NxFileStore`, los tres proyectos `GarbageCollection.*`, `NexusMods.Archives.Nx` y `NexusMods.Paths.Extensions.Nx`
  - [x] **Archivos locales fuera de Descargas:** lo que se agrega con `AddLocalFile` desde otra carpeta (`ManualDownloadRequiredOverlay.cs`, `LibraryViewModel.cs` "agregar desde archivo") no se copiaba a `tModManager/Downloads`, así que quedaba sin `DownloadPath`. Hecho (pieza 3, PR #74): `AddLocalFileJob` copia a Descargas vía `DownloadsFolder.PlaceAsync` (nunca mueve), deduplica por hash, y `LocalFileBackfill` repara los viejos al arrancar
  - [ ] **Backups viejos de NexusMods.App:** `LegacyDataDetector.LegacyBackupsFolder` no se usa; `~/.local/share/NexusMods.App/CyberpunkBackups` nunca se cuenta ni se ofrece borrar. El asistente de limpieza podría mostrarlo y ofrecer borrarlo
- Activas, no requieren acción: `FomodInstaller` (Nexus, commits 2026-09), `GameFinder` y `TransparentValueObjects` (erri120)

## 🎮 Multi-juego (después de limpieza)

Intento anterior falló por acoplamiento a Cyberpunk filtrado fuera de `Games.RedEngine` (~35 archivos). Orden:

- [x] **Renombrar app a tModManager:** app ID `io.github.t4toh.tmodmanager`, data dir `~/.local/share/tModManager/` con migración automática desde `NexusMods.App.Cyberpunk/`, `.desktop` viejo se borra al registrar el handler nxm. Repo GitHub renombrado (ver "Herencia de upstream")
- [x] ~~**CI propio:**~~ sacado el 2026-09-25 (fallaba y se prefiere probar local; la suite equivalente es `./dev.sh` opción 4). Era: GitHub Actions, `.github/workflows/ci.yaml` (ubuntu, `dotnet build -warnaserror`, xUnit vía `dotnet test` con filtro, TUnit vía `dotnet run`). Primera corrida verde: 1175 tests xUnit + 94 TUnit en ~4.5 min. Único arreglo necesario: ordenar hijos antes de `Verify` en `PathBasedInstallerTests` (orden de enumeración difiere entre ext4 y APFS)
- [ ] **Desacoplar Cyberpunk del core** (relevo del 2026-10-03, detalle abajo). Dos reglas: **nada de CP fuera de `Games.RedEngine`** y **nada atado a Nexus**: Nexus Mods es una fuente de mods más (la más popular), no la única; KOTOR vive sobre todo en Deadly Stream, otros mods en GitHub. No hace falta inventar ya una abstracción de "fuentes" (se diseña cuando haya una segunda fuente real), pero no sumar acople nuevo y sacar el que se toque.
- [ ] **Juegos a agregar, en orden** (pedido 2026-09-24; la fase 1 del desacople está hecha: Witcher 3 arranca cuando estén las piezas genéricas que necesita):
  1. **The Witcher 3 Remastered (5.x)** (salió 2026-09-29; expansión *Songs of the Past* en 2027). Investigado 2026-10-03, detalle en "Witcher 3: lo investigado" abajo. El merge de scripts/bundles va último: la edición es muy nueva y las herramientas de la comunidad cambian día a día
  2. **Skyrim SE/AE y Fallout 4, la versión más nueva en Steam** (puede esperar, decidido 2026-10-03: primero los juegos que se van a jugar). Mismo motor: un solo proyecto, FO4 después de Skyrim con sus diferencias. Investigado 2026-10-07, detalle en "Skyrim SE/AE y Fallout 4: lo investigado" abajo. Esfuerzo L (MVP M: Skyrim con `Plugins.txt` ordenado por masters, sin LOOT)
  3. **KOTOR 1 y 2**: juegos viejos que hoy se modean a mano sí o sí (overrides en `Override/`, TSLPatcher/HoloPatcher con instrucciones por mod, orden de instalación estricto). El valor está en automatizar eso. Primero K1 siguiendo el build completo de `kotor.neocities.org` (pedido 2026-10-07: el K1 moddeado a mano que hay en casa es viejo; se rehace en Steam). Investigado 2026-10-03 y 2026-10-07, detalle en "KOTOR 1: lo investigado" abajo. Esfuerzo XL
- [ ] **Requisitos de cada juego como datos, no como wiki** (pedido 2026-10-03): lo que hoy hay que ir a leer a la wiki de cada juego (paquetes de protontricks como `vcrun2022`/`d3dcompiler_47`, DLL overrides, opciones de lanzamiento de Steam, archivos de config a tocar, pasos tras recrear el prefix) declarado por juego, y que la app lo muestre como checklist con estado real y, donde se pueda, un botón que lo haga (`protontricks <appid> -q ...`). Hoy está hardcodeado en `WinePrefixRequirementsEmitter` de CP2077; generalizarlo es parte del desacople (que cada `IGame` declare sus requisitos y el health check sea genérico)
- **No recuperar `Games.CreationEngine` de upstream**: se sacó a propósito porque no gustaba cómo estaba hecho. Escribir cada juego desde cero sobre el core desacoplado; el código viejo (history de NexusMods.App) sirve como mucho de referencia
- [ ] **Referencia Vortex:** `Nexus-Mods/vortex-games` (GPL-3) tiene una carpeta `game-*` por juego (100+) con las reglas de layout/instalación de cada uno; la de Cyberpunk es `E1337Kat/cyberpunk2077_ext_redux` (~25 tipos de layout vs nuestros 4 instaladores). No es código portable (TypeScript/Electron/Windows), son reglas a leer. Para CP2077 sirven: layouts "arreglables" (`.archive` suelto → `archive/pc/mod/`, Redscript sin subcarpeta → `r6/scripts/<mod>/`, DLL suelta → `red4ext/plugins/<mod>/`, REDmod sin `mods/`), mods envueltos en carpeta extra, archivos protegidos (`inputContexts.xml`, `inputUserMappings.xml`, `options.json`) con confirmación, core mods por versión (RED4ext `winmm.dll` vs `d3d11.dll`), CET exige `init.lua`

### Relevo del desacople (2026-10-03)

El core heredado de upstream ya es multi-juego (`IGame`, `SteamLocator`, API de Nexus, nxm, mapeo dominio→juego, Protontricks con appid como parámetro). Lo que ata a CP son agregados del fork cableados por afuera de `IGame` (commits `97480bbf7`, `89368ded9`, `2e7827cad`, `65277f97f`/`0470c083d`, `2520ad2bf`, `723cb0a11`). `App.UI` referencia `Games.RedEngine` desde 4 archivos (`GameWidgetViewModel`, `EssentialModsPage`, `EssentialModsViewModel`, `MyGamesViewModel`).

**Fase 1: arreglos antes de que un segundo juego toque el disco** (hecha el 2026-10-03, rama `feat/decouple-phase1`; cada arreglo con un test de dos juegos donde se pudo):

- [x] **`SortOrderManager` uno por juego** (era singleton y `RegisterSortOrderVarieties` pisaba las variedades del otro juego)
- [x] **`Backups/<GameId>/<timestamp>`**: ruta en un solo lugar (`Sdk/Games/GameBackups.cs`), "conservar el más nuevo" por juego, los snapshots viejos se mueven a `Backups/RedEngine.Cyberpunk2077/` al arrancar (`DataDirectoryMigration.MoveLegacyBackups`)
- [x] **Prefix de Proton y `steam://validate`** tomados de la instalación (`LinuxCompatabilityDataProvider`, `StoreIdentifier`), con la guarda `steamapps/compatdata/<appid>` numérico; el Storage Manager pregunta de qué juego si hay varios de Steam
- [x] **Super Clean**: el límite de un loadout se cuenta por juego. Sin test del caso permitido (correría un Deep Clean real sobre `~/.local/share`: los tests no redirigen XDG)
- [x] **`SupportedGames` y "Enable unsupported games"** borrados: registrar un juego es lo que lo hace soportado
- [x] **Agregar a mano** con selector de juego; valida con `GetPrimaryFile` del juego elegido (si falla, deshace el alta)
- [x] **Identidad del juego = `GameId`**: atributos nuevos `.../Game` en `GameInstallMetadata` y `ManuallyAddedGame`; migración `_0011` los completa desde el id de Nexus (mapeo histórico fijo 3333 → CP, no construye juegos). El id de Nexus viejo queda como `LegacyNexusModsGameId`, que solo lee la migración y `GameRegistry` (para migraciones anteriores a `_0011`). `StubbedGame` ya no tiene id de Nexus y se puede gestionar al lado de CP (`GameWithoutNexusModsIdTests`)
- [x] **Botones de Essentials** y **panel de Wine prefix** solo para el juego que tiene lista/requisitos (el gate sigue siendo "es CP", en un solo lugar cada uno, hasta la fase 2)
- [x] **Borrados globales** del Storage Manager: siguen siendo de todos los juegos, ahora lo dicen los textos. Acotarlos va con "Storage Manager por juego"
- [x] **Fixture de 2 juegos:** no hizo falta una base común; cada test registra el segundo juego que necesita. Borrados los 30 Verify huérfanos de TestFramework (Skyrim/FO4, colecciones viejas, `RedModInstallerTests.*` duplicado): ningún test los generaba

**Fase 2: con Witcher 3 en marcha, abstraer recién cuando haya dos casos reales:**

- [ ] **Herramienta de limpieza** por juego (p. ej. `IDeepCleanTool : ITool`): reemplaza `OfType<CyberpunkDeepCleanTool>()` (`MyGamesViewModel.cs:285`), `IsDeepCleanAvailable` por `GameId` (`GameWidgetViewModel.cs:125`) y el nombre mágico en `StorageAnalyzer`
- [ ] **Essential mods** por juego: mover el record `EssentialMod` fuera de RedEngine, lista opcional (vacía por defecto); que no asuma Nexus (ya tiene fallback de GitHub)
- [ ] **Requisitos del prefix** declarados por juego + un emitter genérico (ver "Requisitos de cada juego como datos")
- [ ] **Sacar `App.UI → Games.RedEngine`** cuando lo anterior esté hecho
- [ ] **Launch sin `IRunGameTool`** explota (`LaunchButtonViewModel.cs:80`, `.First()`): run tool por defecto en `AddGame<T>()`
- [x] **Lista de archivos originales:** hecho (PR #53): `GameBaselineFile` por instalación, de Nexus si conoce la versión, del disco si no (con la propiedad de los mods leída del último loadout aplicado); botón "Actualicé el juego" (`ISynchronizerService.UpdateBaseline`, serializado con los syncs). Texto original: apply en Steam se niega a borrar si la base de hashes no conoce la versión (`ALoadoutSynchronizer.cs:484`). La base viene de `Nexus-Mods/game-hashes` (congelada) y el builder revienta con juegos no registrados (`BuildHashesDb.cs:136`). **Verificar si trae Witcher 3**; si no, usar el estado inicial del disco (`GameInstallMetadata.InitialDiskStateTransaction`) o una base propia. Sin esto no se pueden sacar mods en Witcher
- [ ] **Downloads por juego:** una sola carpeta; el rescan de colecciones fuerza el vínculo cuando coinciden el nombre y el tamaño (`CollectionDownloader.RescanDownloads`; si Nexus no dio tamaño, solo el nombre). Subcarpeta por juego o filtrar candidatos. Lo correcto: guardar el MD5 que trae `collection.json` también para las descargas de Nexus (hoy solo se guarda el de las externas, `HandleExternalDownload`) y vincular por MD5
- [ ] **Storage Manager por juego:** `StorageStats.CyberpunkBackupsSize` → tamaños por juego
- [ ] **GOG/Heroic** (sacado en `723cb0a11`): Witcher 3 y KOTOR se juegan mucho por GOG. Decidir si vuelve o si alcanza con agregar a mano
- [ ] **Menores:** `LegacyDataDetector.LegacyBackupsFolder` (sin uso; mejor usarlo para "Backups viejos de NexusMods.App", arriba), PNG de diseño `cyberpunk_game.png` (va con el ícono genérico). Hechos el 2026-10-07: textos de Welcome, `.desktop`, metainfo y pupnet (genéricos, "por ahora Cyberpunk 2077"; quedan a propósito "Created with the Nexus Mods app" en las colecciones publicadas, que va con la identidad ante Nexus, y los nombres `nexusmods.app.*.log`), referencias duplicadas en `App.csproj`, `FileType.Cyberpunk2077AppearancePreset` borrado (nadie lo usaba: `AppearancePresetInstaller` va por extensión), `TryGetLocatorIdsForVanityVersion` filtra por el app ID de Steam de la instalación

Ya genérico, no tocar: `SteamLocator`, Protontricks, API de Nexus y cookies, nxm, mapeo dominio→juego, migración `_0010`, semáforo del `SynchronizerService` (serializa entre juegos: más lento, correcto). Library ya separa `LocalFile` de `NexusModsLibraryItem`: una fuente nueva es otro tipo de item + su descargador.

### Piezas genéricas para el segundo juego (decidido 2026-10-03)

No se escribe código de Witcher 3 ni de KOTOR hasta que estas piezas existan. Cada una va al core, se prueba primero con CP2077 (que ya tiene un caso real para casi todas) y después la usa W3. Nexus sigue siendo la fuente por defecto, pero ninguna pieza depende de él. Lo que nos diferencia es Linux: herramientas de Windows corriendo en el prefix correcto, Proton y mayúsculas, que es justo lo que Vortex no hace.

| # | Pieza | Prueba con CP2077 | Uso en W3 | Después |
|---|---|---|---|---|
| 1 | Lista vanilla sin la base de Nexus (hecha, PR #53, probada 2026-10-06) (= "Lista de archivos originales" de la fase 2; la base local **no trae W3**) | después de cada parche la app no aplica | poder sacar mods | cualquier juego |
| 2 | Ubicaciones dentro del prefix, con whitelist de archivos gestionados (hecha 2026-10-09, probada en el juego 2026-10-10; `LocationId.WinePrefix` + `IGameData.GetManagedFiles`; spec en `docs/superpowers/specs/2026-10-09-wine-prefix-location-design.md`) | `UserSettings.json` (saves y `modlist.txt` de REDmod cuando haga falta) | saves, `Documents/The Witcher 3/user.settings`, `mods.settings` | cualquier juego con prefix |
| 3 | Mods locales de primera clase (hecha y probada en la app 2026-10-10, PR #74; `LocalFile.Version/Source/PageUri`, copia a Descargas, diálogo al agregar y botón "Editar"; spec en `docs/superpowers/specs/2026-10-10-local-mods-first-class-design.md`) | archivos agregados a mano | mods de mod.io/GitHub/foros | KOTOR |
| 4 | Primer uso real de `IIntrinsicFile` (hecha 2026-10-10, PR #NN, prueba con el juego pendiente; `ASettingsIntrinsicFile<TDoc>` + `IntrinsicFileEntry`/`IntrinsicFileState`, `Ingest` → External Changes y reescritura en el mismo apply; CP2077: `UserSettings.json`; spec en `docs/superpowers/specs/2026-10-10-intrinsic-settings-file-design.md`) | `UserSettings.json` (`loadout settings set`) | `mods.settings`, `dx12user.settings`/`input.settings` (formato INI pendiente), XML de menús | `plugins.txt` (formato de líneas pendiente) |
| 5 | Load order que se escribe a archivo (variedad de sort order + writer) | `modlist` de REDmod | `Priority` de `mods.settings` | Skyrim, orden de patchers KOTOR |
| 6 | Requisitos del prefix como datos (= item de la fase 2) | `WinePrefixRequirementsEmitter` | `dinput8=n,b` para ASI, aviso DLSS bajo Proton | cualquier juego |
| 7 | Runner de herramientas Windows en el prefix (generaliza `GameToolRunner`) | deploy de REDmod | Script Merger, `wcc_lite` | HoloPatcher, xEdit |
| 8 | Archivos derivados: grupo generado con fuentes + hashes, se marca viejo y se regenera | **salida del deploy de REDmod** (hoy entra como "External Changes" y nada la invalida) | `mod0000_MergedFiles` | patchers de KOTOR, bashed patch |
| 9 | Merge 3-way de texto propio (vanilla de base, N-way, archivos con marcadores) | sin equivalente (redscript usa anotaciones) | `.ws` sin Wine | juegos con scripts de texto |

Arquetipos para elegir juegos futuros (cada candidato lleva una ficha: app ID, layout y prefix, arquetipo, herramientas externas, fuentes, qué hacen Vortex/MO2/el manager de la comunidad, piezas que faltan):

- **Archivos sueltos superpuestos** (CP2077, muchos Unity/Unreal): instaladores + 1, 2
- **Carpetas de mod + archivo de orden** (W3, Stardew/SMAPI): + 4, 5
- **Plugins con load order** (Skyrim, Fallout 4): + 5, 7 (xEdit, LOOT), 8 (bashed patch)
- **Cadena de patchers** (KOTOR, Infinity Engine/WeiDU): + 3, 7, 8 re-ejecutándose en orden

### Witcher 3: lo investigado (2026-10-03)

**Juego**
- 5.00 Remastered: solo DX12 (`bin/x64_dx12/witcher3.exe`), scripts/XML/csv/w3strings en UTF-8 (antes UTF-16LE), formato de `.bundle` nuevo (registros 0x140 → 0x130), filelists de menús eliminados, mod.io integrado (dónde guarda en PC: sin documentar)
- REDkit 5.0: overrides por scope (puede volver innecesario el Script Merger), `precompiled.rsblob`, XML con `onConflict`
- Ediciones: re 5.x, ng 4.04 y og 1.32 (betas de Steam). Detección: `launcher-configuration.json` con `remasteredEdition`, exe DX11 = legacy, `bin/config/base/freecamera.ini` = 5.x. Sonda: `content/content0/scripts/game/r4Game.ws`
- Steam 292030 (+499450 GOTY), DLC 378649/378648. ProtonDB Platinum. 5.00 detecta Wine y apaga DLSS/RT/FG; arreglado en Proton Experimental 2026-10-02: diagnóstico

**Dónde va cada cosa**
- `mods/mod*/content/` (nombre con `mod` adelante, 63 caracteres como máximo en 5.x), `dlc/<nombre>/content/` (no va en `mods.settings`), menús en `bin/config/r4game/user_config_matrix/pc/` (en 4.x registrar `name.xml;` en `dx11filelist.txt`/`dx12filelist.txt`, UTF-16), `bin/config/base/*.ini`
- Prefix `compatdata/292030/.../Documents/The Witcher 3/`: `mods.settings`, `input.settings`, `dx12user.settings`, `user.settings` (solo legacy), `gamesaves/`. No existen hasta la primera corrida
- **`[ContentManager/Mods] EnabledLocal=false`** en `dx12user.settings` apaga todo `mods/`: diagnóstico obligatorio; no pisar esas claves

**Load order:** sin `mods.settings`, orden ordinal sin mayúsculas por carpeta, gana el primero. `mods.settings`: `[carpeta] Enabled=0|1 Priority=1..9999`, menor gana, únicas; el juego agrega solo las carpetas desconocidas. Deshabilitar = `Enabled=0` (no renombrar a `~`). `mod0000_MergedFiles` siempre primero

**Instaladores** (Vortex + TW3MM, sin mayúsculas, en orden): rechazar archivos con `WitcherScriptMerger.exe`; XML de menú (saltear copias "backup"); mixto `mod*`+`dlc*`; `…/mods/modX` (quitar lo anterior); `content/` suelto → `mods/mod<Archivo>/content`; todo bajo `dlc*`. No desplegar readmes, `*.part.txt`, `__MACOSX`

**Settings:** fragmentos `input.settings.part.txt`, `user.settings.part.txt`, `dx12user.settings.part.txt` (Vortex/Settings Updater) + regex sobre readmes `.txt` (`[Context]` + `IK_*=(Action=…)`, TW3MM). Merge por sección/clave sobre una base, preservando lo que el usuario cambió en el juego; detectar choques de acción/tecla. En 5.x todo va a `dx12user.settings`

**Scripts y bundles:** conflicto = mismo `content/scripts/<ruta>` en dos o más mods habilitados. Solo hace falta merge si dos o más traen el archivo completo (los que usan `@(wrapMethod|replaceMethod|addMethod|addField)` no). Avisar si la copia del mod no tiene muchas líneas del vanilla actual (umbral SM-FAE: 50, o 10 y 10%). Bundles: leer en C# se puede; escribir `.bundle`/`metadata.store` solo con `wcc_lite` bajo Proton. Primera versión: avisar y correr Script Merger Remastered (Nexus 13076) en el prefix

**Referencias:** Vortex `extensions/games/game-witcher3` (GPL-3: `installers.ts`, `edition.ts`, `menumod.ts`, `contentManager.ts`, `modSettingsPriority.ts`, `scriptStyle.ts`); `Systemcluster/The-Witcher-3-Mod-manager` (BSD-2, soporta Proton); Script Merger IDCs/SM-FAE (GPL-2, confirmar si es "o posterior"); `TheValiantOne/WitcherScriptMerger` (.NET 10, sin interfaz, DiffPlex); W3MM (MIT, merge N-way en `script_merge.rs`; su `mods.settings` está mal)

**Script Merger - Remastered** (Nexus 13076, relapse12, v1.4 del 2026-10-04): entiende los bundles nuevos, scopes, overrides de definiciones XML, `precompiled.rsblob` (detección de conflictos tomada de `Aelto/tw3-cahirb`) y mod.io. Necesita un `wcc_lite.exe` compatible con 5.x: el de REDkit (`bin/x64_RedKit/wcc_lite.exe`) o uno suelto según su `INSTALL.txt`. Sus pruebas: en un conflicto gana el mod de arriba de la lista sin importar el orden alfabético, y **los mods de mod.io quedan siempre arriba**. El autor de SM-FAE (Phaz42) también sacó versión para el Remastered. Doc de la comunidad: "What Does the Remaster Mean for Modding?" (Google Docs, enlazado en la página del mod)

**W3MM de Systemcluster** (rama `Custom`, la default; Python, v0.10.5 del 2026-10-03, activo): soporta Original, Next-Gen y Remastered, y Proton. Ideas para copiar: la instalación de Steam se lanza siempre por Steam (lanzar el exe del Remastered directo crashea); Script Merger se corre en el prefix del juego con `protontricks-launch --appid 292030 <exe>`; si un parche saca el exe configurado, usa el otro renderer de la misma carpeta; los `.ini` de `bin/config/base` los copia enteros, sin merge ni backup (nosotros deberíamos hacerlo mejor)

**Sin verificar:** dónde guarda mod.io (el orden ya está, ver arriba), orden de las carpetas DLC, si `--launcher-skip` sigue andando en 5.x, si el compilador de REDkit corre sin interfaz bajo Wine, impacto de *Songs of the Past*

### KOTOR 1: lo investigado (2026-10-07)

**Enfoque: la guía como receta.** Lo difícil de KOTOR (conflictos entre patchers que editan `dialog.tlk`/`.2da`/`.mod` y no se desinstalan) ya lo resolvieron los autores del build fijando orden y opción por mod. La app no decide nada: ejecuta una receta lineal. La guía exige partir de un juego limpio, así que reconstruir desde vanilla es lo que ya manda. Aplicar = desde la lista vanilla (pieza 1), correr los pasos en orden **en un staging**, minúsculas, diff contra el paso anterior y guardar lo que cambió como archivos derivados (pieza 8) con clave hash(estado previo + mod). Sacar o mover el mod N recalcula desde N; lo de antes sale del cache. El synchronizer despliega el resultado. Nadie hace esto: los mantenedores del build dicen que un manager de verdad es "virtually impossible" y que no se use Vortex

**Juego**
- Steam 32370, solo Windows: Proton. `steamapps/common/swkotor`; saves (`Saves/`) y `swkotor.ini` dentro de la carpeta del juego, no en el prefix: excluirlos de la lista vanilla. `override/` no existe hasta el primer mod; sin subcarpetas en `override/` ni `modules/`; gana el último escrito; un `.mod` pisa a su `.rim`
- Minúsculas: bajo Proton Wine ignora mayúsculas, el riesgo son duplicados que difieren solo en eso. Pasar todo a minúsculas al desplegar y detectar duplicados sin distinguir mayúsculas
- **El exe de Steam está cifrado:** el parche de 4GB lo rompe salvo que antes se aplique widescreen; después del widescreen hay que lanzar el exe directo (se rompe el handshake con Steam); HR Menus solo anda en GOG, 4 discos, Mac o Steam parcheado con UniWS. El exe de GOG es la misma 1.03 sin DRM (Tatoh tiene las dos copias). Hipótesis a probar: exe de GOG sobre la instalación de Steam
- GOG (Windows, por Heroic/Wine) no hace falta soportarlo para esto: alcanza con su exe

**El build** (`github.com/KOTOR-Community-Portal/mod-builds`, Markdown en `content/k1/full.md`, **sin LICENSE**; deploy a Neocities)
- Revision 12 (2025-11-01) y parches cada 2-4 semanas (v12.3.15 del 2026-09-16). Mantienen Snigaroo, JCarter426 y LS1
- K1 full: 197 entradas con campos fijos (`**Name:**`, `**Author:**`, `**Category & Tier:**`, `**Installation Method:**`, `**Masters:**` = dependencias) y notas en bloques `:::note`/`:::warning`. El orden de instalación es el del documento. 112 archivos sueltos, 58 TSLPatcher, 13 HoloPatcher, 3 multi-corrida, ~11 mixtos. 82 traen instrucciones en prosa ("borrá X antes de copiar", "elegí la opción 2", "este va al directorio del juego, no a override")
- Descargas: Deadly Stream ~82%, Nexus ~11%, Mega ~5% (más en el spoiler-free), sueltos en GitHub/Drive
- `content/linux.md` (guía de Linux del mismo sitio): Proton-GE 10.34 (no Experimental), lanzamiento `MANGOHUD_CONFIG="fps_limit=72,no_display" mangohud %command%` (60 si el monitor es de 60 Hz), minúsculas en los mods y en `override/` al pasar de un patcher a archivos sueltos, patchers con `protontricks-launch --appid 32370 ./*.exe`, al final borrar `.tpc` con `.tga`/`.dds` del mismo nombre y las borraduras de Character Textures & Model Fixes. Cuelgues en cinemáticas: renombrar `movies/`
- Setup fijo, igual para todos: instalación limpia (también `compatdata/32370`), una sola instalación de K1, widescreen (UniWS) + HR Menus + Widescreen Fade/Main Menu fixes, recién después 4GB. Va a la pieza 6 (requisitos como datos)
- Lo que cambia entre parches del build sale del diff de git: solo se recuran esas entradas

**La curación de los 197 pasos es el trabajo de verdad, no el código.** Atajo: **KOTORganizer** (plugin de MO2, `J0-o/kotorganizer`, sin licencia; [hilo](https://deadlystream.com/topic/12202-toolkotorganizer-mo2-plugin/)) ya tiene el K1 full curado en `J0-o/kson_modlist`: formato `kotor-builder-instructions` v1, 200 mods, ~13.5k acciones mover/borrar/renombrar por archivo con hashes xxh3, nombre de archivo, versión, URL y `tslpatch_order`; actualizado junto con el build (último 2026-10-05). Sin licencia: pedirle permiso al autor antes de usarlo

**Patchers en C#, sin Python en la app**
- **KPatcher** (`KotORPublicDomain/KPatcher`, LGPL-3): port de HoloPatcher a C#. Mismos parámetros que HoloPatcher (`--game-dir --tslpatchdata --namespace-option-index --install`), la interfaz de línea está dentro del exe de la UI (`src/KPatcher.UI/KPatcherCLI.cs`). Riesgos: ~98k líneas en `KPatcher.Core` escritas casi todas por Copilot, `LangVersion 7.3`, sin nullable, net9, deps Newtonsoft/YamlDotNet/sly/SharpCompress, sin releases, submódulo `vendor/TSLPatcher` sin licencia (no traerlo). Su ledger dice paridad "PARTIAL": compila NSS con un compilador propio, no `nwnnsscomp`
- Plan: **KPatcher como proceso aparte**, compilado por nosotros desde un commit fijo (self-contained linux-x64). No meterlo en el core. Paridad medida, no creída: un test de desarrollo corre KPatcher y `uvx holopatcher==1.5.3` sobre los mismos mods y compara hashes. Python solo ahí
- HoloPatcher (`OpenKotOR/PyKotor`, LGPL; `NickHugi/PyKotor` abandonado): backup por mod y desinstalación solo de la última instalación (LIFO): no sirve para desinstalar, la cadena reproducible lo reemplaza
- Plan B para mods donde KPatcher falle: el TSLPatcher original en el prefix con `protontricks-launch` (pieza 7), con clics
- Parches de exe nativos: 4GB = bit `IMAGE_FILE_LARGE_ADDRESS_AWARE` del header PE; widescreen/HR Menus cambian constantes de resolución (falta ver qué bytes toca UniWS). El exe pasa a ser derivado (original + parches), nunca se edita en el lugar
- Formatos si hacen falta: `NickHugi/Kotor.NET` (GPL-3, C#, sin patcher). `xoreos-tools` (GPL-3) para inspeccionar
- No usables: KOTORModSync v2+ (BSL 1.1; el fork `CrispyW0nton/KotorModSync` trae P2P oculto con UPnP y telemetría; el original da 404), BioWare.NET (BSL hasta 2029). Vortex `game-sw-kotor` (GPL-3, archivado) rechaza todo mod con `tslpatchdata`: sirve solo para la detección del juego. `ChristopherVR/kotor-mod-manager` (GPL-3, Python/Tauri, muy activo) hace el flujo del build pero no lo recomiendan en el Discord; sus directivas (`installer/build_directives.py`) son referencia

**Deadly Stream** (Invision Community): baja **sin login** (la página da cookie de sesión y un link `?do=download&csrfKey=…`; con varios archivos hay un selector). Sin API pública (claves solo del admin), RSS de novedades en `files.xml`, `robots.txt` solo bloquea dotbot, los términos no hablan de bots. Bajar un mod por clic del usuario es lo que hace un navegador; antes de bajar en lote, preguntar a los administradores. Nexus (`kotor`, 686 mods) cubre solo ~11% del build

**Etapas** (las 1 y 2 no escriben en la carpeta del juego)
1. Deadly Stream como fuente: pegar un link, se baja a la biblioteca con nombre/autor/versión/URL; updates por RSS o la página. Es la segunda fuente real: recién ahí diseñar la abstracción de fuentes
2. El build como lista de compras: leer `full.md` de un tag fijo, mostrar qué está bajado, qué falta y qué cambió; bajar lo que falta
3. Registrar K1 (Steam, Proton) + instalador de archivos sueltos a `override/` en minúsculas sin readmes ni previews + mods locales (pieza 3)
4. Cadena reproducible con KPatcher (piezas 5 y 8), setup fijo y parches de exe (pieza 6)
5. Después: K2 (build de Windows por Proton; el nativo de 2015 no anda bien con mods)

**Pruebas en la PC con Linux antes de escribir código**
- [ ] Exe de GOG sobre el K1 de Steam: ¿arranca desde Steam con Proton-GE?
- [ ] Ese exe con 4GB + widescreen: ¿sigue arrancando desde Steam?
- [ ] KPatcher sin interfaz con K1CP y 2-3 mods TSLPatcher con opciones sobre una copia de K1; lo mismo con HoloPatcher en otra copia; comparar hashes de `override/`, `dialog.tlk`, `modules/`

**Preguntar (los manda Tatoh):** al autor de KOTORganizer, permiso para usar los KSON; a los administradores de Deadly Stream, si se puede bajar en lote

**Sin verificar:** qué bytes cambia UniWS en el exe de Steam (los pasos están en un video), si `--uninstall` de KPatcher/HoloPatcher pide confirmación, mayúsculas exactas que trae la instalación de Steam, si Deadly Stream limita descargas

### Skyrim SE/AE y Fallout 4: lo investigado (2026-10-07)

**Enfoque: la colección como receta, LOOT como juez.** Arquetipo "plugins con load order". El equivalente a la guía de KOTOR son las colecciones de Nexus (ya las instalamos para CP2077): traen qué plugins activar y reglas de LOOT, no un orden literal; Vortex las aplica corriendo el sort de LOOT con esas reglas. Wabbajack es otra cosa: una foto byte a byte de una instancia de MO2 (parches OctoDiff, BSA recreados, perfil de MO2); queda para después, si queda. Lo que más duele no es el load order sino **Steam actualizando el juego**: rompe el script extender y todo plugin DLL hasta que cada autor recompila, y en FO4 la mayoría de las colecciones grandes siguen pidiendo la versión vieja

**Juegos** (Steam, Proton; GOG queda afuera como el resto del fork)
- **Skyrim SE** 489830 (DLC AE 1746860, Creations 598120), Nexus `skyrimspecialedition` 1704. Versión 1.7.104 (2026-08-27); antes 1.7.99 (2026-08-20) y 1.6.1170 (2024-01). Una sola rama en Steam, sin betas para volver. Detección: FileVersion de `SkyrimSE.exe`
- **Fallout 4** 377160 (Creations Bundle 3868650), Nexus `fallout4` 1151. Versión 1.11.240 (2026-08-18). Ramas que importan: old-gen 1.10.163, next-gen 1.10.984, AE 1.11.x. Una sola rama en Steam. **Del top 100 de colecciones de FO4, 54 piden 1.10.163** (la #1, StoryWealth, 921 mods, 126 GB, actualizada 2026-09-09); volver ahí es con downgraders de terceros (Simple Fallout 4 Downgrader 0.5, delta patches de Nexus 98059, FO4Down con DepotDownloader y login de Steam). Detección: FileVersion de `Fallout4.exe`
- Bloquear updates: `appmanifest_<appid>.acf` de solo lectura (`chattr +i` en Linux); mover la biblioteca le saca el flag

**Layout** (todo en la carpeta del juego salvo lo marcado)
- `Data/`: plugins `.esm/.esp/.esl` (tipo por flags del header TES4: `0x1` master, `0x200` light; no por extensión), BSA (Skyrim) o BA2 (FO4), sueltos ganan sobre archivos. Límites: 254 plugins completos + 4096 light
- **`Plugins.txt`** en el prefix, `AppData/Local/Skyrim Special Edition/` o `AppData/Local/Fallout4/`: formato asterisco (todas las líneas, `*` = activo, el orden de las líneas es el load order), **Windows-1252**, Skyrim lo lee con P mayúscula. Sin `loadorder.txt` en estos dos. Implícitos, que no van en el archivo y cargan primero en orden fijo: los ESM base y DLC (lista fija por juego) más lo que liste `Skyrim.ccc`/`Fallout4.ccc` (carpeta del juego). Si un ini define `sTestFile*`, FO4 ignora `Plugins.txt`
- INIs y saves en el prefix, `Documents/My Games/Skyrim Special Edition/` (`Skyrim.ini`, `SkyrimPrefs.ini`, `SkyrimCustom.ini`, `Saves/`) o `Documents/My Games/Fallout4/` (`Fallout4*.ini`, `Saves/*.fos` + cosave `.f4se`)
- **Creations:** los CC gratis vienen en los depots; las pagas se bajan desde el menú del juego **directo a `Data/`** (`cc*.esl/.esm/.bsa/.ba2`; Skyrim además `Creations/*.manifest`). Para el synchronizer son External Changes: el reset y Deep Clean no pueden tratarlas como basura (contenido pago del usuario)
- Mayúsculas: Wine no distingue, el riesgo es desplegar `Meshes/` y `meshes/` como carpetas distintas. Al desplegar, unificar contra la carpeta que ya existe

**Script extenders** (SKSE64 / F4SE, de ianpatt)
- Actuales: SKSE 2.3.1 para 1.7.104 (2026-08-27), F4SE 0.7.9 para 1.11.240; para old-gen FO4, F4SE 0.6.23 + BASS (Backported Archive2 Support System, si no los BA2 v7/v8 crashean). Cada uno trae `<x>_loader.exe` + `<x>_<versión del exe>.dll` a la raíz; los plugins van en `Data/SKSE/Plugins` / `Data/F4SE/Plugins`, junto con **Address Library** (`version-<runtime>.bin`, una por versión del exe; Skyrim v13 todo en uno)
- **No se pueden redistribuir** (el readme lo prohíbe, ni en colecciones): se bajan de Nexus (30379 / 42147) o silverlock como cualquier mod. Los builds nuevos de SKSE no están en los releases de GitHub. Encaja en "Essential mods por juego" (fase 2) con link y chequeo, sin bajarlos solos
- Diagnóstico clave: versión del exe vs la DLL del extender y vs los `.bin` de Address Library. Es lo primero que rompe cada parche de Steam
- Lanzar bajo Proton: GE-Proton/umu ya cambia `SkyrimSELauncher.exe` por `skse64_loader.exe` si existe; con Proton de Valve, renombrar el launcher o una opción de lanzamiento. FO4: lo mismo con `Fallout4Launcher.exe`. Va a requisitos (pieza 6) o al run tool del juego

**Diferencias de FO4** (además de nombres de carpeta y F4SE)
- Sueltos: `[Archive] bInvalidateOlderFiles=1` y `sResourceDataDirsFinal=` en `Fallout4Custom.ini` (MO2 y Vortex lo siguen escribiendo; si sigue haciendo falta en AE: sin verificar). Pieza 4 sobre un ini del prefix
- BA2 v1 (old-gen) vs v7/v8 (next-gen/AE): en 1.10.163 un v7/v8 crashea sin BASS; se lee la versión en los primeros bytes. Límite de 256 BA2, 1024 desde 1.11.240
- Light plugins: rango de FormID según HEDR (≥ 1.0 en FO4, 1.71 en Skyrim)
- Precombines/previs (PRP, scripts de PJM con el Creation Kit): un mod que pisa celdas precombinadas rompe el rendimiento. Avisar; generarlos es pieza 8 con el CK por Proton, mucho después
- Versión de juego de la colección vs la instalada: avisar antes de bajar 100 GB que no van a andar

**Load order**
- Upstream (`Games.CreationEngine`, borrado en `7ecfc93ed`, se mira con `git show 7ecfc93^:src/NexusMods.Games.CreationEngine/...`): `PluginsFile` como `IIntrinsicFile`, orden topológico propio (masters primero, ESM < ESL < ESP **por extensión**), Mutagen solo para leer masters, `Ingest` sin hacer, sin implícitos ni `.ccc`, y **las rutas de AppData/My Games no apuntaban al prefix** (el `SteamLocator` calculaba el prefix y no lo pasaba). Diagnósticos "Missing Master"/"Disabled Master". No traerlo; sirve para ver qué no repetir
- MVP sin LOOT: lector propio del header TES4 (flags, `MAST`, `HEDR`, unas 50 líneas) en vez de Mutagen (GPL-3, net10, pero ~23 MB entre Skyrim y FO4 + Rx + DynamicData). Orden: implícitos, masters antes que dependientes, ESM/light antes que el resto por flags; lo demás respeta el orden del usuario. `Ingest` lee el `Plugins.txt` que haya tocado el juego (el menú Creations lo reescribe: sin verificar)
- **LOOT** (`loot/libloot`, GPL-3, ahora en Rust): sin API C ni `.so` oficial para Linux; Vortex compila su propio `libloot.so` desde 2026-09-23. Opciones: shim Rust `cdylib` propio + P/Invoke (necesita toolchain Rust en el build), o el Flatpak de LOOT con `--auto-sort` (trabaja sobre el `Plugins.txt` real). Masterlists `loot/skyrimse` y `loot/fallout4`: **CC0**, YAML crudo de GitHub, rama `v0.29`
- Colecciones: `CollectionRoot` hoy solo lee `info`, `mods` y `modRules`. Para Bethesda faltan `plugins` (`[{name, enabled}]`), `pluginRules` (`{plugins: [{name, group, after, req, inc}], groups}`, esquema de la userlist de LOOT) y `collectionConfig`. Vortex activa todos los plugins de la colección salvo `enabled: false` y ordena con LOOT + esas reglas; sin LOOT, las reglas `after` alcanzan para un orden razonable
- Ganchos que ya esperan a Bethesda en el core: `enableallplugins` del FOMOD se ignora (`FomodXmlInstaller.cs:217`), `GetExtenderVersion`/`IsExtenderPresent` sin implementar (`ContextDelegates.cs:41-66`), firmas BSA/BA2/TES4 en `Signatures.cs`, `IIntrinsicFile` sin ningún juego que lo use

**Herramientas y archivos derivados** (pieza 7 para correrlas, pieza 8 para su salida; en MO2 cada salida es un "mod de salida" que se regenera)
- Nativas en Linux: LOOT (Flatpak, CLI `--auto-sort`), Wrye Bash v315 (GPL-3, Python, Linux desde la 312, consciente de Proton; Bashed Patch `Bashed Patch, 0.esp`), BodySlide 5.9.1 (meshes y `.tri`), Pandora (hay build Linux, recomiendan la de Windows)
- Por Proton: xEdit/SSEEdit/FO4Edit 4.1.5f (MPL-2.0), DynDOLOD/TexGen (`DynDOLOD.esm/.esp`, `Occlusion.esp`), PGPatcher (ex ParallaxGen; lee su salida anterior para conservar FormIDs), Nemesis (casi abandonado). Synthesis es WPF: Linux no soportado
- Requisitos del prefix que aplica MO2-LINT para Skyrim: `xact`, `d3dcompiler_43`, `d3dcompiler_47`, override `xaudio2_7=n,b` (audio de NPC que falta). ENB: `dxgi=n,b;d3d11=n,b`; DXVK 3.1.1 arregla el crash de shaders con ENB. GE-Proton 10-14 recomendado por Jackify para ENB

**Referencias:** Corkscrew (`cashcon57/corkscrew`, GPL-3, Rust/Tauri, v0.14.x; despliega con hardlink → reflink → copia, sin symlinks para `.exe`/`.dll`; `plugins/skyrim_se.rs`, `deployer.rs`, `wabbajack_directives.rs`); libloadorder (`Ortham/libloadorder`, `src/game_settings.rs`: implícitos, carpetas por tienda, métodos de load order); Vortex `extensions/games/game-skyrimse`/`game-fallout4`, `collections/types/ICollection.ts`, `util/gameSupport/gamebryo.tsx`, `gamebryo-archive-invalidation` (el repo `vortex-games` está archivado); MO2-LINT (`Furglitch/modorganizer2-linux-installer`, `configs/game_info.yml`: versiones de DLL por runtime y protontricks); umu-protonfixes `gamefixes-steam/489830.py`. Wabbajack: código GPL-3 en NuGet (`Wabbajack.DTOs` sirve para leer listas), listas CC BY-NC-SA, el instalador manda métricas y `Wabbajack.Compression.BSA` trae ImageSharp (licencia dividida): solo referencia. Jackify (Wabbajack en Linux, alfa). Fluorine Manager (port de MO2 a Linux con FUSE)

**Etapas** (las piezas 2, 4, 5 y 6 tienen que estar)
1. Skyrim SE: registrar (Steam, Proton), ubicaciones en el prefix (`AppData/Local/Skyrim Special Edition`, `My Games/Skyrim Special Edition`), instalador `Data/` + archivos del extender a la raíz (FOMOD ya existe; atender `enableallplugins`), `Plugins.txt` como intrinsic con implícitos y `.ccc`, run tool por el loader, diagnósticos (masters faltantes, extender/Address Library vs exe, Creations como External Changes protegidas)
2. FO4 sobre lo mismo, old-gen y AE: tabla por rama, invalidación en `Fallout4Custom.ini`, versión de BA2 vs rama (BASS en old-gen), F4SE, aviso de versión de colección vs instalada, bloqueo de updates en old-gen
3. Colecciones Bethesda: `plugins`/`pluginRules`, orden por reglas
4. LOOT con masterlist (shim o Flatpak)
5. Herramientas y salidas derivadas: Wrye Bash, xEdit, BodySlide, Pandora, DynDOLOD, PGPatcher; precombines de FO4 al final
6. Quizá: Wabbajack

**Pruebas en la PC con Linux antes de escribir código**
- [ ] Skyrim + SKSE: ¿Proton de Valve o GE-Proton? ¿el cambio automático de umu anda desde Steam?
- [ ] ¿El juego reescribe `Plugins.txt` (orden, mayúscula, encoding) al usar el menú Creations o con un plugin que no existe?
- [ ] ¿Dónde caen las Creations pagas bajadas desde el juego, y aparece `ContentCatalog.txt` en AppData?
- [ ] FO4 1.11.240: ¿hace falta `bInvalidateOlderFiles` para que carguen los sueltos?
- [ ] FO4: ¿Simple Fallout 4 Downgrader o los delta patches corren bajo Linux/Proton partiendo de 1.11.240?

**FO4: las dos ramas** (decidido 2026-10-07, por popularidad: AE es lo que da Steam, old-gen lo que piden la mayoría de las colecciones). La rama es un dato de la instalación (FileVersion del exe), no un juego aparte: mismo `GameId`, y lo que cambia por rama sale de una tabla (DLL de F4SE esperada, `.bin` de Address Library, versiones de BA2 que carga, si hace falta BASS). Al instalar una colección, comparar su versión de juego con la rama instalada. El downgrade lo hace una herramienta de terceros, no la app; la app detecta la rama, avisa y, en old-gen, ofrece bloquear updates (requisito, pieza 6). Mismo criterio para Skyrim si aparecen colecciones para 1.5.97

**Sin verificar:** si `bInvalidateOlderFiles` sigue haciendo falta en AE, si el juego reescribe `Plugins.txt` desde el menú Creations, `ContentCatalog.txt`, si los downgraders de FO4 andan desde 1.11.240, si la CLI de Wabbajack corre nativa en Linux, si Corkscrew aplica parches OctoDiff de listas nuevas, si Mutagen lee bien BA2 v7/v8 y headers 1.71, permisos de Address Library, si bajar SKSE/F4SE por la API de Nexus pide Premium, SKSE 2.2.8 vs 2.3.0 para 1.7.99 (las fuentes no coinciden)

## 🧬 Herencia de upstream a nivel repo

Hecho el 2026-09-22 (rama `feat/rename-tmodmanager`): borrados `.github/` completo (dependabot, issue templates, 17 workflows, scripts), submódulos `extern/SMAPI` y `docs/Nexus`, `docs/` + `mkdocs.yml`, `scripts/`, `codecov.yaml`, `qodana.yaml`, `CHANGELOG.md`, `CONTRIBUTING.md`, `NexusMods.App.sln.DotSettings`, `Nexus-Icon.png`, `.idea/` (untrackeado). `NuGet.Build.props` reducido a `GenerateDocumentationFile`. README con atribución a NexusMods.App (GPL-3.0). PR #25 de dependabot cerrado.

- [x] **Renombrar repo GitHub** `cp2077-mm` → `tModManager` (2026-09-22, GitHub redirige la URL vieja). URLs actualizadas en `metainfo.xml` y `app.pupnet.conf`
- [x] **Limpieza GitHub** (2026-09-22): borrados 3 deployments fallidos + environment `test` (los disparó un workflow de release de upstream el 2026-02-09 sobre la rama `remove-stuff`, antes de borrar `.github/`); borrados los 51 tags heredados de upstream (`v0.0.1`..`v0.21.1`, `0.6.1-temp`), quedan solo los 6 del fork con release (`v0.22.0`..`v0.23.4`); wiki y projects deshabilitados. El workflow dinámico "Dependabot Updates" desaparece solo. `dev.sh` opción 10 pasa `--app-version` desde el último tag git (antes el AppImage salía siempre como 1.0.0)
- [ ] **Issue templates propios** (bug + feature) si hace falta

## 🐛 Errores conocidos y deuda

- [ ] **Pieza 4, límites conocidos (PR #NN):** al deshabilitar un mod con entradas, la clave conserva el último valor (la base absorbe lo que el juego reescribió) hasta que el juego o el usuario la cambien; revertir de verdad pediría guardar el valor previo al primer Write por clave. Una entrada editada entre un Write y el siguiente Ingest puede leerse como cambio del juego (ventana chica). Un archivo que no parsea y no tiene base se deja en paz sin log (la clase base no tiene logger). Pendientes: formatos INI/líneas, UI de entradas, orden por load order entre entradas, productores desde instaladores/colecciones

- [ ] **Pieza 3, menores diferidos de la revisión (PR #74, 2026-10-10):** mover "Editar" del toolbar al menú "…" de la fila (ahí viven las acciones del ítem: página, changelog, borrar; el toolbar le suma una segunda navegación); el esquema de `PageUri` solo se valida en el diálogo (el CLI `--url` y `OpenUri` aceptan cualquier URI absoluta: exigir http(s) en `ApplyMetadata`/`UpdateLocalFileMetadata`); sin toast cuando un alta reutiliza un ítem existente (y el nombre prellenado pisa el que tenía); cada alta se espera antes de abrir el siguiente diálogo; un nombre en blanco cae al nombre del archivo en Downloads (puede ser `Mod_1.zip`), mejor `Path.GetFileName(OriginalPath)`; `Uri ==` ignora el fragmento (un cambio solo de `#ancla` no se guarda); `catch (Exception)` en `AddFilesFromDisk` también muestra toast al cancelar; `out var unused` → `out _`; un symlink dentro de Downloads elegido desde adentro se registra tal cual (`InFolder` léxico, preexistente); `NexusMods.DataModel.Tests` reporta un Total distinto en cada corrida (xUnit v3, 0 fallos, preexistente)

Estado al 2026-10-07:

- **Issues abiertos en GitHub:** 0
- **Build:** 0 errores, 0 warnings de compilador (solo `NU19xx` de auditoría NuGet, ver arriba)
- **Comentarios `TODO`/`FIXME` en `src/`:** 75
- **`Debug.Assert`/`Debug.Fail` en `src/`:** 106

Encontrado el 2026-09-22 con el juego real (instalación anterior modeada, restaurada por Steam con solo 20 MB de descarga):

- [x] **Deep Clean no cubre todo.** Cubierto (2026-09-24, `.nx` removal PR 3): `CyberpunkDeepCleanTool` ahora mueve la raíz del juego (todo archivo no vanilla), `r6/audioware/`, `r6/input/`, `r6/config/cybercmd/`, `r6/config/redsUserHints/`, `r6/publishing/` (diffado archivo por archivo, no wholesale: la base ya tiene vanilla ahí), `r6/logs/`, INIs de `engine/config/platform/pc/`, `engine/config/base/scripts.ini`, `r6/cache/final.redscripts*`, `r6/cache/input*.xml`, `tools/redmod/tweaks/**/devices.tweak`, `bin/x64/CyberPunk.bat`, todo contra el set vanilla de `IFileHashesService`.
- [x] **Instaladores dejan readmes en la raíz del juego** (arreglado 2026-10-07: `SimpleOverlayModInstaller` saltea `.txt`/`.md`/`.pdf`/`.png`/`.jpg`/`.jpeg`/`.url` en la raíz del mod, la misma lista que ya ignoraba `FolderlessModInstaller`; FOMOD sigue instalando lo que su XML pida). Deep Clean limpiaba esos readmes/imágenes después del hecho (item de arriba), pero los instaladores los seguían deployando ahí. Vortex los manda a una carpeta aparte (`SpecialExtraFiles`); nosotros deberíamos ignorarlos o no deployarlos. Confirmado con la colección real: Apply dejó 4 readmes en la raíz (`FlatlinedExit_readme.txt`, `ItemRecordsFixes_readme.txt`, `Slaughtomatic_*_readme.txt`)
- [ ] **Identidad ante Nexus** (investigado 2026-10-07, se decide "cuando estemos más firmes"): mandamos `Application-Name: NexusModsApp` (`ApplicationConstants.DefaultUserAgentName`) y el `client_id` OAuth `"nma"`, los dos de la app oficial. La [API Acceptable Use Policy](https://help.nexusmods.com/article/114-api-acceptable-use-policy) prohíbe textualmente "request metadata which ... impersonates another application" y pide registrar las apps públicas (support@nexusmods.com, build de prueba). Cambiarlo a `tModManager` nos hace identificables (bloqueables por nombre); con el hack de descarga adentro un registro no se aprobaría. El hack (`GenerateDownloadUrl`) va con User-Agent de Firefox y no lleva estos headers. Decidir junto con el item de abajo
- [ ] **OAuth client_id propio.** `Auth/OAuth.cs:21` usa `"nma"`, el client de la app oficial; Nexus podría revocarlo. Registrar uno para tModManager si Nexus lo permite (probablemente no den clients a forks). Anotado 2026-09-24
- [ ] **Tests no deben tocar estado real del usuario.** Ya pasó dos veces (`.desktop` en #32, `Temp/` en #33). Revisar el resto de `AddDefaultServicesForTesting` + `AddOSInterop` real: `xdg-settings set` sigue corriendo en tests

Incidente 2026-09-25, primera prueba real del asistente de limpieza:

- [x] **"Borrar prefix de Proton" borró parte de `~/.local/share`.** `DeleteDirectory(recursive: true)` de NexusMods.Paths sigue symlinks y el prefix trae `dosdevices/z: -> /`. Se cortó a los 13 s por un archivo de solo lectura de flatpak. Perdido: KWallet (`kwalletd`), baloo y todo lo de `.local/share` creado antes que `flatpak`; `klipper` y `kactivitymanagerd` rescatados de `/proc/*/fd`; repo flatpak del usuario dañado (`flatpak repair --user`). `@home` no tenía snapshots. Arreglado: `DeleteDirectoryNoFollow` en todos los borrados recursivos, Deep Clean ignora archivos detrás de symlinks, tamaño de backups sin seguir links, tests con symlink hacia afuera. Prevención: `sudo snapper -c home create-config /home`
- [x] **Auditoría de todo lo que borra/mueve/sobrescribe** (misma rama, 3 agentes + verificación propia). Arreglado, cada uno con test que falla sin el fix:
  - `SevenZipExtractor.FixPaths` usaba los nombres crudos del archivo: una entrada `/ruta/.` o `../../x/.` borraba esa carpeta en cualquier lado del disco
  - `..` en rutas de mods (FOMOD, `collection.json`) escribía fuera del juego: `GameLocations.ToAbsolutePath` lo rechaza y los dos `RunActions` validan todo antes de tocar el disco (también que no haya carpetas-symlink en el medio)
  - El escaneo del juego ya no entra en carpetas enlazadas (antes: limpiar/desgestionar/cambiar de loadout borraba lo que había detrás del link); `ExtractFiles` reemplaza un symlink en el destino en vez de escribir a través
  - Sin lista vanilla (parche de Steam que la base de hashes no conoce, o juego agregado a mano) no hay reset, "Limpiar carpeta" ni apply en Steam: antes borraba el juego entero. Desde #53 la lista sale del disco si la base no conoce la versión, y el botón "Actualicé el juego" la rehace después de un parche
  - Nombres con `\` en descargas y en entradas de archivos comprimidos (salían de la carpeta), zip de la base de hashes, mudanza de descargas viejas
  - GC y "Borrar archivos" del store: solo el primer nivel, sin links. `uninstall-app` ya no borra la Storage Location elegida, una DB que no sea RocksDB, `Backups/` ni `Downloads/`. "Borrar descargas" solo borra lo registrado en la biblioteca. Reset de la DB vieja exige marcadores de RocksDB antes de borrar nada
  - Deep Clean: nada debajo de carpetas-symlink; si un movimiento al backup falla (juego en otro disco) corta antes de tocar la DB o podar backups
  - Configs, logs, temp y base de hashes salen de `NexusMods.App/` (compartido con la app oficial) a `tModManager/`, con copia única al arrancar; el limpiador de PID viejo solo mata si el proceso sigue siendo tModManager
  - Se sacó la ubicación AppData (apuntaba a `~/.local/share` nativo, no al prefix)
- [ ] **Deep Clean `Execute` sin test de integración:** `BackupsRoot` usa el `XDG_DATA_HOME` real, así que un test escribiría (y `PruneOldBackups` podría borrar) los backups reales del usuario. Primero hacer inyectable la carpeta de backups
- [ ] **Fixture aislado de sync: archivos `archive/pc/...` de `AddModAsync` quedan en `WarnOfUnableToExtract`** (no se consideran en el store) mientras `bin/...` se despliega. Investigar si es algo del fixture o un bug real con `.archive`
- [ ] **Validar la Storage Location elegida** (rechazar `/`, `$HOME`, raíces XDG, bibliotecas de Steam). Con el GC ya acotado no es destructivo, pero mezcla el store con datos del usuario
- [x] **Rechazar `..` al instalar el mod**, no solo al aplicar (2026-10-07): antes un solo mod con `..` hacía fallar el apply de todo el loadout. `SafePath.ThrowIfParentSegment` en los tres instaladores que toman el destino de datos del mod: destinos del XML de FOMOD, `hashes[].path` de `collection.json` (`InstallCollectionDownloadJob.InstallReplicatedMod`) y `FallbackCollectionDownloadInstaller`. El mod no se instala y el error dice cuál y qué ruta
- [ ] **Diagnóstico para carpetas-symlink dentro del juego:** el escaneo ya no entra ahí y escribir debajo bloquea el apply; mostrar qué carpeta es en vez de solo el error
- [ ] **El asistente mueve `NexusMods.App/Downloads` de la app oficial** (paso 2): si la oficial se sigue usando le rompe la biblioteca. Copiar, o mover solo lo que la base de tModManager referencia
- [ ] **Limpiador de PID viejo compara `ProcessName`:** bajo `dotnet NexusMods.App.dll` el nombre es `dotnet`. Comparar `/proc/<pid>/exe` con el ejecutable propio (ojo: el AppImage monta en un `/tmp/.mount_*` distinto cada vez)
- [x] **Cookie de Nexus en el argv de curl** (`NexusApiClient.cs`): era visible en `ps` para otros usuarios locales. Desde 2026-10-07 va por stdin (`-H @-`, curl 7.55+)
- [ ] **7zz 21.03 (2021) empaquetado.** Rechaza links peligrosos (probado), pero es viejo: hay CVEs posteriores (p. ej. zstd, links en ZIP). Actualizar a 25.x

Encontrado el 2026-09-24 en la eliminación de `.nx` (revisiones de implementación):

- [ ] **Doble apertura con un reset pendiente.** Dos lanzamientos dentro de la ventana de migración pueden actuar ambos como main; el segundo puede borrar la base recién creada por el primero (se pierde solo esa sesión). Arreglo: lock exclusivo sobre el marker de reset, o reclamar el slot de instancia única antes de resolver `MigrationService`
- [ ] **`CleanupUnresponsiveProcesses` (heredado de upstream) mata con SIGKILL** el PID anotado en el archivo de sync si el heartbeat tarda más de 6s. Desde el 2026-09-25 solo si el proceso sigue llamándose tModManager (un PID reusado ya no muere); un main propio ocupado todavía puede morir
- [ ] **Storage Manager: botones sin `CanExecute` atado a `IsBusy`** (solo guard dentro del cuerpo); el botón de cerrar ventana sigue activo mientras corre un paso del asistente
- [x] **Deep Clean: `Directory.Move` falla entre filesystems** (librería de Steam en otro disco o subvolumen btrfs): arreglado el 2026-10-03 con `NoFollowMove`. Sigue abierto: el `final.redscripts.bk` de redscript viejo se mueve pero un `final.redscripts` modeado se queda (Steam verify lo arregla)
- [ ] **Collections: `PackageReExtractionTests` no corre `InstallCollectionJob` ni el handler `nxm://`** (desde #55 prueba el helper compartido `RestoreCollectionPackageAsync`, pero no el borrado de la entrada vieja ni la bajada de nuevo; hace falta un fixture de `CollectionRevisionMetadata` sin red)
- [x] **Deep Clean borra "My Mods" y la biblioteca no puede instalar** (visto 2026-10-04, arreglado el mismo día): Deep Clean vacía las colecciones editables en vez de borrarlas (`CyberpunkDeepCleanTool.RemoveModGroups`); la biblioteca, si no queda ninguna (loadouts ya limpiados, o "My Mods" borrada a mano con otra colección presente), instala sin destino y `InstallLoadoutItemJob` crea una sola "My Mods" bajo lock aunque se instale en paralelo
- [x] **Archivos extraídos con permisos `000`** (visto 2026-10-04, arreglado el mismo día): la versión nueva de AdaptiveSliders guarda sus `.reds` con modo unix 0 y 7zz los restauraba tal cual, así que `AddLibraryFileJob.HashAsync` fallaba con `UnauthorizedAccessException`. `FileExtractor.ExtractAllAsync` ahora da `u+rw` (`u+rwx` en carpetas) a todo lo extraído, sin seguir symlinks
- [x] **Descarga de la página del mod en vez del archivo** (visto 2026-10-04, no era un bug de descarga): el warning de `HttpDownloadJob` logueaba `DownloadPageUri` (la página del mod, que solo se guarda) en vez de la URL que realmente baja; los ~370 KB eran el archivo del CDN, que se trabó y se reanudó bien. Ahora loguea la URL del archivo (sin la query firmada) y la página aparte
- [x] **"Borrar descargas" deja lo que la biblioteca no registró** (visto 2026-10-04, arreglado el mismo día): tras un reset de la base, de 501 archivos borró 284 y dejó 217 sin registrar. Ahora, si la carpeta es la de tModManager (`DownloadsSettings.DefaultFolder`, y no es un symlink), se borra todo lo de primer nivel; si es una carpeta elegida por el usuario, solo lo registrado. Los `.tmp-` a medio escribir se respetan en los dos casos
- [x] **Log `Remaining Limit: 0` engañoso** (visto 2026-10-04, arreglado el mismo día): un header `x-rl-*` ausente se parseaba como 0. `NexusApiClient.ParseHeaders` ahora loguea el endpoint y el límite solo si vino el header ("no rate limit headers" si no); el próximo log dice qué endpoint es cuál. Confirmado con la prueba del 2026-10-04: las llamadas sin header son todas `users.nexusmods.com/oauth/userinfo` (19 en 5 min); `api.nexusmods.com/v1` sí lo manda (~19900)
- [x] **Descargas que fallan al actualizar mods** (visto 2026-10-04, arreglado el mismo día: `GenerateDownloadUrlThrottle`): con "Actualizar" sobre muchos mods, Cloudflare responde a `GenerateDownloadUrl` con su página "Just a moment..." y la descarga falla (solo queda un `DEBUG` "returned non-JSON ... skipping"; el usuario no ve por qué). El semáforo de `CallCurlGenerateDownloadUrlAsync` serializa las llamadas pero no las espacia: las que salen ~100 ms después de la anterior reciben el desafío, las separadas por ≥0,7 s pasan casi siempre (log del 2026-10-04 11:22: 39 de 65 rechazadas). Arreglo: dentro del semáforo, mínimo ~1 s entre llamadas y reintento con espera creciente (2/4/8 s) si vuelve HTML; loguear como `WARN` cuando se agotan los reintentos. Test con un `curl` falso que devuelva HTML las primeras veces. Probado en real el 2026-10-06 (PR #54): colección de 283 mods, 283 `GenerateDownloadUrl` aceptados a la primera, ningún reintento; 7 cortes del CDN ("No data received for 60s") retomados solos
- [x] **Agregar una colección desde `nxm://` fallaba con `MissingArchiveException`** (visto 2026-10-06, arreglado en #55 y probado con la colección real): la biblioteca conservaba el paquete de la colección pero su `collection.json` ya no estaba en el store (Archives borrado a mano en la prueba del 03/10) ni el paquete en Descargas ("Borrar descargas"). El handler reusaba esa entrada sin reextraer. Ahora `NexusModsLibrary.RestoreCollectionPackageAsync` (compartido con `InstallCollectionJob`) reextrae del paquete y, si no puede, el handler borra la entrada vieja y vuelve a bajar
- [ ] **"Instalar" del panel Wine prefix sin prefix:** si el juego todavía no recreó `pfx/` (prefix recién borrado), el botón igual corre protontricks y falla. Mostrar "Lanzá el juego una vez desde Steam" en vez del botón. Visto 2026-10-07. Dentro de la jaula no puede andar (protontricks escribe en `~/.cache` y el Steam Linux Runtime sus lockfiles en `steamapps/common/SteamLinuxRuntime_*`): ese paso se prueba afuera
- [x] **Jugar y Aplicar habilitados mientras se instala un mod o una colección** (visto 2026-10-07: Jugar durante la instalación de la colección y la app se colgó; arreglado el mismo día): mientras corre el job el loadout sigue en `Current` (todavía no hay nada commiteado), así que `ApplyControlViewModel` habilitaba Jugar. Ahora los dos botones esperan a que no haya `IInstallLoadoutItemJob` ni `InstallCollectionJob` activos para ese loadout; al terminar, el loadout queda con cambios y Jugar sigue deshabilitado hasta aplicar. Sin cubrir: el "Instalar" de protontricks del panel Wine prefix no bloquea Jugar
- [x] **El primer toast de cada sesión no se veía, y ninguno tenía color** (visto 2026-10-07 probando #57, arreglado el mismo día): `WindowNotificationService` creaba el `WindowNotificationManager` de Avalonia en el primer `ShowToast` y le mostraba el toast antes de que tuviera template (lo recibe en el siguiente pase de layout), así que se perdía sin error; además ignoraba `ToastNotificationVariant` y recortaba el texto a una línea. Ahora el primer toast espera al dispatcher, éxito/falla muestran una barra verde/roja (`SuccessStrongBrush`/`DangerStrongBrush`), las fallas duran 10 s y el texto ocupa hasta 4 líneas
- [x] **El rescan salteaba descargas de menos de 1 KiB** (visto 2026-10-07, arreglado el mismo día): *Buzzsaw VFX Fix* (447 bytes) se volvía a bajar aunque estaba en `Downloads/`; había 31 archivos así. Ahora `RescanDownloads` saltea solo los vacíos. Test: `RescanDownloadsTests` (arma la colección en la base, sin red)
- [x] **La biblioteca tiraba `An item with the same key has already been added` con la clave de la colección** (visto 2026-10-07, 88 errores al abrir la biblioteca tras el rescan y reinstalar 6 mods de la colección; arreglado el mismo día): la columna "Colecciones" (`LibraryDataProviderHelper.ObserveInstalledCollectionNames`) re-clavaba los ítems del loadout por su colección, y dos ítems de la misma colección (dos archivos de una página de mod, o un archivo instalado dos veces) daban la misma clave. Ahora deduplica con `DistinctValues`. Test: `LibraryInstalledCollectionsTests`. Sin confirmar qué dato nuevo lo disparó (nunca había aparecido en los logs)
- [x] **Instalar desde la biblioteca un mod sin descarga ni store falla en silencio** (visto 2026-10-04 probando el PR #53, arreglado 2026-10-07): tras "Borrar descargas", instalar *Buzzsaw VFX Fix* tiraba `InvalidOperationException` ("Faltan 1 archivo(s)… la descarga no está o cambió de contenido. Volvé a bajar el mod.") desde `InstallLoadoutItemJob` y llegaba como "unhandled exception in R3" (6 clics, parecía que el botón no hacía nada). Ahora `LibraryViewModel.InstallLibraryItem`, por donde pasan el botón de la fila y el de instalar lo seleccionado, la loguea y muestra un toast de error con el mensaje
  - [ ] La biblioteca sigue ofreciendo como instalables ítems cuya descarga ya no existe: marcarlos o filtrarlos (va junto con "Limpiar biblioteca")

### Otros TODO relevantes en código

- [ ] **Pieza 2, menores diferidos de la revisión (PR #73, 2026-10-10):** `GameLocations.IsManaged` quedó entre el `<summary>` de `ToAbsolutePath` y el método (mover el bloque); `GameLocationsService.IndexGame` no chequea `IsManaged` tras `ToGamePath` (un prefix anidado dentro de `Game` indexaría todo el prefix como paths `WinePrefix`; falla seguro en el guard); el filtro de `CleanDirectories` no tiene test que falle sin él; la rama `$USER` de `WineUserName` (prefix de Lutris) no tiene test; `FilesToIndex` chequea `FileExists` antes de `IsUnderSymlink` (solo metadata; invertir el orden)
- [ ] **Prefix manual (Lutris) borrado + reinicio:** `ManuallyAddedLocator` solo declara el prefix si la carpeta existe, así que la base queda con paths `WinePrefix` de una ubicación no declarada. Sync y unmanage fallan con mensaje claro (`EnsureDiskChangesStayInside`), pero `DiffTreeViewModel.ToAbsolutePath` puede tirar `KeyNotFoundException` en la UI. Declararlo por la ruta guardada exista o no, como hace `SteamLocator`
- [ ] **Symlinks de archivo en la carpeta del juego:** el scan los lista y los hashea a través del link; un mod que los reemplace deshace el link (`LooseFileStore.ExtractFiles` lo borra antes de escribir) y el reset borra el archivo. El prefix ya lo rechaza (`EnsureDiskChangesStayInside`, solo ubicaciones con whitelist); extender a `Game` cuando se decida qué hacer con links legítimos
- `NexusMods.Library/DownloadsService.cs:46` — restaurar descargas completadas desde storage al arrancar
- `NexusMods.Networking.NexusWebApi/NexusModsLibrary.Collections.cs:237-259` — metadata de colección hardcodeada (`AdultContent`, `Summary`, `Author`)
- `NexusMods.Networking.NexusWebApi/LoginManager.cs:303` — diálogo de "necesitás login" para operaciones
- `NexusMods.Backend/FileExtractor/Extractors/SevenZipExtractor.cs:264` — sin reporte de progreso
- `NexusMods.Games.RedEngine/RedModDeployTool.cs:89` — usa sort order del loadout en vez del "Active"
- `NexusMods.Games.RedEngine/Cyberpunk2077/SortOrder/RedMod/RedModSortOrderVariety.cs:87-267` — criterio de ganador por `ModGroupId` más reciente, mejorar
- `NexusMods.Abstractions.Games/SortOrder/ASortOrderVariety.cs:147,190` — sin retry ante data race en transacción
- `NexusMods.Games.RedEngine/Cyberpunk2077/Emitters/PatternBasedDependencyEmitter.cs:75` — usar index scan ordenado
- `NexusMods.App.UI/Settings/ExperimentalSettings.cs:11` — remover para GA
- `NexusMods.Backend/FileExtractor/FileExtractor.cs` (`ExtractAllAsync`) — traga la cancelación de cada intento de extractor y relanza `FileExtractionException` en vez de `OperationCanceledException`; el re-extractor lo esquiva con `ThrowIfCancellationRequested` explícito
