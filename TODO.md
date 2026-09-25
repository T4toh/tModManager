# TODO

## 📍 Estado y próximos pasos (2026-09-25)

Mergeado: eliminación de `.nx` en 3 PRs (#42 store por hash + GC, #43 descargas propias, #45 asistente de limpieza + Deep Clean reforzado) y #44 (snapshot de RedMod). Sin CI: los workflows de GitHub se sacaron, **la verificación es local** (`./dev.sh` opción 4, un proyecto por vez). Nada de esto se probó todavía con la app abierta: solo build + tests.

### Mañana: probar todo en esta PC (Linux, juego real)

Antes de arrancar: backup de saves (`steamapps/compatdata/1091500/pfx/drive_c/users/steamuser/Saved Games/CD Projekt Red/Cyberpunk 2077/`) y de `~/.local/share/tModManager/` (el asistente borra la base y los `.nx`).

1. **Build y suite local.** `git pull && ./dev.sh` → opción 4 (tests sin red/flakey, proyecto por proyecto). Esperado: todo verde. Después `dotnet build -c Release` o AppImage (opción 10).
2. **Asistente de datos viejos** (hay 285 `.nx` en `DataModel/Archives`, tiene que aparecer al abrir):
   - Paso 1 aviso → Siguiente.
   - Paso 2: muestra cantidad/tamaño de `NexusMods.App/Downloads` → Siguiente las **mueve** a `tModManager/Downloads` (chequear que el origen quede vacío y los nombres se conserven).
   - Paso 3: Deep Clean (backup en `tModManager/Backups/<timestamp>/`). Probar sin tildar el prefix de Proton. Si falla, anotar el error y probar "Continuar sin limpiar".
   - Paso 4: "Verificar integridad en Steam" (abre Steam) → esperar que termine → "Reiniciar".
   - Al volver: sin asistente, app vacía (base nueva), no quedan `*.nx`.
3. **Gestionar el juego e instalar "Welcome to Night City".** Anotar cuántos mods reutiliza el MD5 rescan (descargas rescatadas) y cuántos baja. Nada debería bajarse dos veces.
4. **Aplicar y lanzar el juego** desde la app: Redscript, CET y RED4ext sin errores, jugable.
5. **Disco:** `find ~/.local/share/tModManager -name '*.nx'` vacío; existe `DataModel/Archives/XX/<hash>`; `Downloads/` con los nombres de Nexus.
6. **Store borrado a mano:** con la app cerrada, borrar `DataModel/Archives/`; abrir, Aplicar → se reextrae todo desde Descargas. Repetir con la app **abierta** (borrar y aplicar): no tiene que fallar el sync.
7. **Reinstalar la colección después de borrar el store:** no tiene que volver a bajar nada de Nexus.
8. **Storage Manager:** Deep Clean → `Downloads/` intacta y el backup nuevo presente en `tModManager/Backups`. "Borrar descargas" pide confirmación. "Borrar prefix de Proton" solo con el juego cerrado (opcional, pierde saves no sincronizados).
9. **Logs:** `~/.local/state/tModManager/Logs/nexusmods.app.main.current.log` (antes de la rama de seguridad del 2026-09-25 estaban en `NexusMods.App/Logs`). Pegar cualquier excepción en la sesión.

Notas: login desde un build de `bin/` necesita `/etc/dotnet/install_location` apuntando a `~/.dotnet` (ya está en esta PC). Si algo crashea solo en Debug con `Assertion failed`, es un `Debug.Assert`: anotar cuál.

### Pendiente después de probar

- [ ] **Una descarga con contenido cambiado se da por bajada.** `CollectionDownloader.ValidateStatusAsync` solo mira que exista el archivo de `DownloadPath`; si el contenido cambió, la reextracción lo rechaza (hash) y la instalación de un mod suelto (`InstallLoadoutItemJob.RestoreMissingArchiveEntries`) solo loguea y deja correr los instaladores (puede instalar con layout incorrecto). Arreglo de una línea: tirar error claro cuando `restored < missing`
- [ ] **Super Clean con varios loadouts:** hoy se niega (guarda del 2026-09-25). Arreglo real: conservar todos los snapshots de una corrida (cada pasada con sync crea uno y `PruneOldBackups` se lleva el primero, el único con archivos no gestionados). El orden de snapshots es por nombre (hora local): un cambio de horario o una carpeta ajena en `Backups/` puede elegir mal
- [ ] `InstallCollectionDownloadJob.TryReExtractMissingFiles` pasa `default` en vez del token del job a `RestoreAsync`
- [ ] **Desacoplar Cyberpunk del core** (ver sección Multi-juego). Rama nueva desde `main`, verificada con la suite local.

## ✅ Completado

- [x] Descarga automatizada de colecciones (sin premium, sin browser)
- [x] Captura de enlaces NXM (protocolo independiente)
- [x] Vista unificada de descargas
- [x] Diagnósticos de mods esenciales (Redscript, RED4ext, CET, ArchiveXL, TweakXL, Codeware)
- [x] Deep Clean + Storage Manager
- [x] Remoción total de telemetría
- [x] Rebrand a "Cyberpunk 2077 Mod Manager"
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
- [ ] **Refactorizar CollectionDownloadViewModel:** ~850 líneas. Extraer lógica de orquestación a un servicio separado
- [ ] **Unificar settings de paralelismo:** `DownloadSettings.MaxParallelDownloads` solo controla descargas de colección, no las regulares

## 🎨 Mejoras de UI/UX

- [ ] **Tema "cueva": negro + violeta/índigo.** Reemplazar la paleta naranja/gris de Nexus por fondo negro y acentos violeta/índigo. Colores en `src/NexusMods.Themes.NexusFluentDark/Resources/` (src/NexusMods.Themes.NexusFluentDark/Resources/Palette/Colors/BrandColors.axaml src/NexusMods.Themes.NexusFluentDark/Resources/Palette/Colors/ElementColors.axaml ); los controles referencian brushes con nombre, así que es cambiar la paleta, no los controles. Pedido 2026-09-22
- [ ] **Ícono nuevo** para reemplazar `src/NexusMods.App/icon.svg` e `icon.ico` (heredados de Nexus). Base: el de tWriter, `~/Repos/tWriter/src/assets/icon.png` (también `src-tauri/icons/*.png` en varios tamaños). Misma línea visual que el tema cueva. Actualizar también `io.github.t4toh.tmodmanager.metainfo.xml` si cambia el nombre del ícono. Pedido 2026-09-22

- [ ] **Botón "Borrar prefix de Proton"** en Storage Manager, junto al Deep Clean: borra `steamapps/compatdata/1091500/` (Steam lo recrea al lanzar). Con confirmación: se pierden saves no sincronizados con la nube y toda la config del prefix. Pedido 2026-09-22

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
- [ ] **Bugs runtime reales:** pendiente reproducir en Linux con juego instalado (crashes esporádicos reportados). Hipótesis a verificar: hay 108 `Debug.Assert`/`Debug.Fail` en `src/`; en build Debug (`dotnet run`, `dev.sh`) cualquier assert fallido mata el proceso. El AppImage es Release y no los ejecuta. Si los crashes son corriendo desde `dev.sh`, correr con `-c Release` para descartar
- [x] **Paquetes al día** (2026-09-24, PRs #38, #39, #40): vulnerabilidades NuGet a 0, bumps dentro del major, Microsoft.Extensions 10, Humanizer 3, StrawberryShake 16, tests a xunit v3 / TUnit 1.x / Verify 32, Paths 0.22.5. Retenidos a propósito: TreeDataGrid 11.1.1 (11.2+ es comercial, Avalonia Accelerate), FluentAssertions 7.x (8 es comercial), Verify 32.x (33+ trae SponsorCheck que rompe el build), Fomod 1.2.1 (solo Windows), MnemonicDB 0.28.2 (ver abajo)
- [ ] **Avalonia 12** (+ ReactiveUI 24, SkiaSharp 4, Splat 21): migrar ~259 `[Reactive]` de ReactiveUI.Fody (muerto) a ReactiveUI.SourceGenerators; TreeDataGrid 12 es comercial, así que vendorizar el fuente MIT de 11.1 (`AvaloniaUI/Avalonia.Controls.TreeDataGrid`, archivado) y portarlo. Sin apuro mientras Avalonia 11 reciba parches (11.3.22 el 2026-09-11)

## 📦 Dependencias heredadas de Nexus

Decidido 2026-09-24. Todas son GPL-3.0 como tModManager: se pueden vendorizar (copiar el fuente a `src/`) sin problema legal. Criterio: **congeladas en la última versión que funciona; se vendorizan cuando haya un motivo concreto** (bug, vulnerabilidad en una dependencia nativa, versión de .NET que las rompa), no antes.

- **MnemonicDB** (la base: loadouts, mods, colecciones): repo archivado 2025-11. Quedamos en **0.28.2**, la última que usó la app oficial en producción. **No subir a 0.50+**: es una reescritura de API publicada dos semanas antes de archivar, ninguna app real la usó. Si hace falta tocarla, vendorizar el tag `v0.28.2`. Depende de RocksDB 9.10 y DuckDB (vigilar sus advisories). Largo plazo opcional: reemplazar por SQLite (~286 archivos la usan)
- **NexusMods.Paths** (`AbsolutePath`, `GamePath`, filesystem en memoria para tests): sin commits desde 2025-10. Congelada en **0.22.5**. Ojo: 0.22 trae su propio `ChunkedStream`/`IChunkedStreamSource` (este último en el namespace global); usamos el nuestro de `NexusMods.Sdk.IO`, calificado
- **NexusMods.Hashing.xxHash3**: reemplazable por `XxHash3` de `System.IO.Hashing` (paquete oficial de Microsoft). Hacerlo junto con la eliminación de `.nx`, porque los hashes también se guardan en la base
- [x] **Eliminar `.nx` file store** (2026-09-24, PRs #42, #43, #45; falta la prueba con la app, ver arriba): reemplazado por `LooseFileStore` (content-addressed, `Archives/<2-hex>/<hash>`) + GC por barrido (`LiveHashes`). Descargas de primera clase en `tModManager/Downloads` con `LibraryFile.DownloadPath` y reextracción vía `IDownloadReExtractor`. Asistente de limpieza guiada para datos viejos (`.nx`, DB vieja), Deep Clean reforzado, borrado del prefix de Proton. Salen `NxFileStore`, los tres proyectos `GarbageCollection.*`, `NexusMods.Archives.Nx` y `NexusMods.Paths.Extensions.Nx`
  - [ ] **Archivos locales fuera de Descargas:** lo que se agrega con `AddLocalFile` desde otra carpeta (`ManualDownloadRequiredOverlay.cs`, `LibraryViewModel.cs` "agregar desde archivo") no se copia a `tModManager/Downloads`, así que queda sin `DownloadPath`: no se puede reextraer si se borra del store ni es portable. Copiarlo (o moverlo) a Descargas antes de agregarlo
  - [ ] **Backups viejos de NexusMods.App:** `LegacyDataDetector.LegacyBackupsFolder` no se usa; `~/.local/share/NexusMods.App/CyberpunkBackups` nunca se cuenta ni se ofrece borrar. El asistente de limpieza podría mostrarlo y ofrecer borrarlo
- Activas, no requieren acción: `FomodInstaller` (Nexus, commits 2026-09), `GameFinder` y `TransparentValueObjects` (erri120)

## 🎮 Multi-juego (después de limpieza)

Intento anterior falló por acoplamiento a Cyberpunk filtrado fuera de `Games.RedEngine` (~35 archivos). Orden:

- [x] **Renombrar app a tModManager:** app ID `io.github.t4toh.tmodmanager`, data dir `~/.local/share/tModManager/` con migración automática desde `NexusMods.App.Cyberpunk/`, `.desktop` viejo se borra al registrar el handler nxm. Pendiente: renombrar el repo GitHub `cp2077-mm` → `tModManager` (manual, GitHub redirige)
- [x] ~~**CI propio:**~~ sacado el 2026-09-25 (fallaba y se prefiere probar local; la suite equivalente es `./dev.sh` opción 4). Era: GitHub Actions, `.github/workflows/ci.yaml` (ubuntu, `dotnet build -warnaserror`, xUnit vía `dotnet test` con filtro, TUnit vía `dotnet run`). Primera corrida verde: 1175 tests xUnit + 94 TUnit en ~4.5 min. Único arreglo necesario: ordenar hijos antes de `Verify` en `PathBasedInstallerTests` (orden de enumeración difiere entre ext4 y APFS)
- [ ] **Desacoplar Cyberpunk del core:** mover refs detrás de `IGame`. Sitios: `DataModel/Storage/StorageAnalyzer.cs`, `DataModel/DataModelSettings.cs`, `SchemaVersions/_0010_FixDeepCleanDisabledItems.cs`, `Sdk/FileExtractor/Signatures.cs`, `Abstractions.Games/SortOrder/*`, UI (`StorageManager`, `EssentialMods`, `MyGames`, `Welcome`, `ManualAddGame`, `GameWidget`), `SingleProcess/CliSettings.cs`, `App/Services.cs`, `App/Program.cs`
- [ ] **Juegos a agregar, en orden** (pedido 2026-09-24; recién cuando agregar un juego sea posible, o sea después del desacople):
  1. **Skyrim, la versión más nueva en Steam** (Special/Anniversary Edition): SKSE, `plugins.txt`/load order, FOMOD (ya existe), Proton. Fallout 4 comparte motor y queda casi gratis después
  2. **The Witcher 3** (next-gen, después del parche nuevo, preparándose para el DLC): mods en `mods/` + `dlc/`, merges de scripts (Script Merger), menús en `bin/config/r4game/user_config_matrix/pc/`
  3. **KOTOR 1 y 2**: juegos viejos que hoy se modean a mano sí o sí (overrides en `Override/`, TSLPatcher/HoloPatcher con instrucciones por mod, orden de instalación estricto). El valor está en automatizar eso
- **No recuperar `Games.CreationEngine` de upstream**: se sacó a propósito porque no gustaba cómo estaba hecho. Escribir cada juego desde cero sobre el core desacoplado; el código viejo (history de NexusMods.App) sirve como mucho de referencia
- [ ] **Referencia Vortex:** `Nexus-Mods/vortex-games` (GPL-3) tiene una carpeta `game-*` por juego (100+) con las reglas de layout/instalación de cada uno; la de Cyberpunk es `E1337Kat/cyberpunk2077_ext_redux` (~25 tipos de layout vs nuestros 4 instaladores). No es código portable (TypeScript/Electron/Windows), son reglas a leer. Para CP2077 sirven: layouts "arreglables" (`.archive` suelto → `archive/pc/mod/`, Redscript sin subcarpeta → `r6/scripts/<mod>/`, DLL suelta → `red4ext/plugins/<mod>/`, REDmod sin `mods/`), mods envueltos en carpeta extra, archivos protegidos (`inputContexts.xml`, `inputUserMappings.xml`, `options.json`) con confirmación, core mods por versión (RED4ext `winmm.dll` vs `d3d11.dll`), CET exige `init.lua`

## 🧬 Herencia de upstream a nivel repo

Hecho el 2026-09-22 (rama `feat/rename-tmodmanager`): borrados `.github/` completo (dependabot, issue templates, 17 workflows, scripts), submódulos `extern/SMAPI` y `docs/Nexus`, `docs/` + `mkdocs.yml`, `scripts/`, `codecov.yaml`, `qodana.yaml`, `CHANGELOG.md`, `CONTRIBUTING.md`, `NexusMods.App.sln.DotSettings`, `Nexus-Icon.png`, `.idea/` (untrackeado). `NuGet.Build.props` reducido a `GenerateDocumentationFile`. README con atribución a NexusMods.App (GPL-3.0). PR #25 de dependabot cerrado.

- [x] **Renombrar repo GitHub** `cp2077-mm` → `tModManager` (2026-09-22, GitHub redirige la URL vieja). URLs actualizadas en `metainfo.xml` y `app.pupnet.conf`
- [x] **Limpieza GitHub** (2026-09-22): borrados 3 deployments fallidos + environment `test` (los disparó un workflow de release de upstream el 2026-02-09 sobre la rama `remove-stuff`, antes de borrar `.github/`); borrados los 51 tags heredados de upstream (`v0.0.1`..`v0.21.1`, `0.6.1-temp`), quedan solo los 6 del fork con release (`v0.22.0`..`v0.23.4`); wiki y projects deshabilitados. El workflow dinámico "Dependabot Updates" desaparece solo. `dev.sh` opción 10 pasa `--app-version` desde el último tag git (antes el AppImage salía siempre como 1.0.0)
- [ ] **Issue templates propios** (bug + feature) si hace falta

## 🐛 Errores conocidos y deuda

Estado al 2026-09-22:

- **Issues abiertos en GitHub:** 0
- **Build:** 0 errores, 0 warnings de compilador (solo `NU19xx` de auditoría NuGet, ver arriba)
- **Comentarios `TODO`/`FIXME` en `src/`:** 99

Encontrado el 2026-09-22 con el juego real (instalación anterior modeada, restaurada por Steam con solo 20 MB de descarga):

- [x] **Deep Clean no cubre todo.** Cubierto (2026-09-24, `.nx` removal PR 3): `CyberpunkDeepCleanTool` ahora mueve la raíz del juego (todo archivo no vanilla), `r6/audioware/`, `r6/input/`, `r6/config/cybercmd/`, `r6/config/redsUserHints/`, `r6/publishing/` (diffado archivo por archivo, no wholesale: la base ya tiene vanilla ahí), `r6/logs/`, INIs de `engine/config/platform/pc/`, `engine/config/base/scripts.ini`, `r6/cache/final.redscripts*`, `r6/cache/input*.xml`, `tools/redmod/tweaks/**/devices.tweak`, `bin/x64/CyberPunk.bat`, todo contra el set vanilla de `IFileHashesService`.
- [ ] **Instaladores dejan readmes en la raíz del juego.** Deep Clean ahora limpia esos readmes/imágenes después del hecho (item de arriba), pero los instaladores los siguen deployando ahí. Vortex los manda a una carpeta aparte (`SpecialExtraFiles`); nosotros deberíamos ignorarlos o no deployarlos. Confirmado con la colección real: Apply dejó 4 readmes en la raíz (`FlatlinedExit_readme.txt`, `ItemRecordsFixes_readme.txt`, `Slaughtomatic_*_readme.txt`)
- [ ] **OAuth client_id propio.** `Auth/OAuth.cs:21` usa `"nma"`, el client de la app oficial; Nexus podría revocarlo. Registrar uno para tModManager si Nexus lo permite (probablemente no den clients a forks). Anotado 2026-09-24
- [ ] **Tests no deben tocar estado real del usuario.** Ya pasó dos veces (`.desktop` en #32, `Temp/` en #33). Revisar el resto de `AddDefaultServicesForTesting` + `AddOSInterop` real: `xdg-settings set` sigue corriendo en tests

Incidente 2026-09-25, primera prueba real del asistente de limpieza:

- [x] **"Borrar prefix de Proton" borró parte de `~/.local/share`.** `DeleteDirectory(recursive: true)` de NexusMods.Paths sigue symlinks y el prefix trae `dosdevices/z: -> /`. Se cortó a los 13 s por un archivo de solo lectura de flatpak. Perdido: KWallet (`kwalletd`), baloo y todo lo de `.local/share` creado antes que `flatpak`; `klipper` y `kactivitymanagerd` rescatados de `/proc/*/fd`; repo flatpak del usuario dañado (`flatpak repair --user`). `@home` no tenía snapshots. Arreglado: `DeleteDirectoryNoFollow` en todos los borrados recursivos, Deep Clean ignora archivos detrás de symlinks, tamaño de backups sin seguir links, tests con symlink hacia afuera. Prevención: `sudo snapper -c home create-config /home`
- [x] **Auditoría de todo lo que borra/mueve/sobrescribe** (misma rama, 3 agentes + verificación propia). Arreglado, cada uno con test que falla sin el fix:
  - `SevenZipExtractor.FixPaths` usaba los nombres crudos del archivo: una entrada `/ruta/.` o `../../x/.` borraba esa carpeta en cualquier lado del disco
  - `..` en rutas de mods (FOMOD, `collection.json`) escribía fuera del juego: `GameLocations.ToAbsolutePath` lo rechaza y los dos `RunActions` validan todo antes de tocar el disco (también que no haya carpetas-symlink en el medio)
  - El escaneo del juego ya no entra en carpetas enlazadas (antes: limpiar/desgestionar/cambiar de loadout borraba lo que había detrás del link); `ExtractFiles` reemplaza un symlink en el destino en vez de escribir a través
  - Sin lista vanilla (parche de Steam que la base de hashes no conoce, o juego agregado a mano) no hay reset, "Limpiar carpeta" ni apply en Steam: antes borraba el juego entero. **Consecuencia: después de un parche la app no aplica hasta tener hashes de la versión nueva**
  - Nombres con `\` en descargas y en entradas de archivos comprimidos (salían de la carpeta), zip de la base de hashes, mudanza de descargas viejas
  - GC y "Borrar archivos" del store: solo el primer nivel, sin links. `uninstall-app` ya no borra la Storage Location elegida, una DB que no sea RocksDB, `Backups/` ni `Downloads/`. "Borrar descargas" solo borra lo registrado en la biblioteca. Reset de la DB vieja exige marcadores de RocksDB antes de borrar nada
  - Deep Clean: nada debajo de carpetas-symlink; si un movimiento al backup falla (juego en otro disco) corta antes de tocar la DB o podar backups
  - Configs, logs, temp y base de hashes salen de `NexusMods.App/` (compartido con la app oficial) a `tModManager/`, con copia única al arrancar; el limpiador de PID viejo solo mata si el proceso sigue siendo tModManager
  - Se sacó la ubicación AppData (apuntaba a `~/.local/share` nativo, no al prefix)
- [ ] **Deep Clean `Execute` sin test de integración:** `BackupsRoot` usa el `XDG_DATA_HOME` real, así que un test escribiría (y `PruneOldBackups` podría borrar) los backups reales del usuario. Primero hacer inyectable la carpeta de backups
- [ ] **Fixture aislado de sync: archivos `archive/pc/...` de `AddModAsync` quedan en `WarnOfUnableToExtract`** (no se consideran en el store) mientras `bin/...` se despliega. Investigar si es algo del fixture o un bug real con `.archive`
- [ ] **Validar la Storage Location elegida** (rechazar `/`, `$HOME`, raíces XDG, bibliotecas de Steam). Con el GC ya acotado no es destructivo, pero mezcla el store con datos del usuario
- [ ] **Rechazar `..` al instalar el mod** (`FomodXmlInstaller.cs` destinos, `FallbackCollectionDownloadInstaller`), no solo al aplicar: hoy un solo mod con `..` hace fallar el apply de todo el loadout (no borra nada, pero bloquea)
- [ ] **Diagnóstico para carpetas-symlink dentro del juego:** el escaneo ya no entra ahí y escribir debajo bloquea el apply; mostrar qué carpeta es en vez de solo el error
- [ ] **El asistente mueve `NexusMods.App/Downloads` de la app oficial** (paso 2): si la oficial se sigue usando le rompe la biblioteca. Copiar, o mover solo lo que la base de tModManager referencia
- [ ] **Limpiador de PID viejo compara `ProcessName`:** bajo `dotnet NexusMods.App.dll` el nombre es `dotnet`. Comparar `/proc/<pid>/exe` con el ejecutable propio (ojo: el AppImage monta en un `/tmp/.mount_*` distinto cada vez)
- [ ] **Cookie de Nexus en el argv de curl** (`NexusApiClient.cs`): visible en `ps` para otros usuarios locales. Pasarla por `-H @archivo` o stdin
- [ ] **7zz 21.03 (2021) empaquetado.** Rechaza links peligrosos (probado), pero es viejo: hay CVEs posteriores (p. ej. zstd, links en ZIP). Actualizar a 25.x

Encontrado el 2026-09-24 en la eliminación de `.nx` (revisiones de implementación):

- [ ] **Doble apertura con un reset pendiente.** Dos lanzamientos dentro de la ventana de migración pueden actuar ambos como main; el segundo puede borrar la base recién creada por el primero (se pierde solo esa sesión). Arreglo: lock exclusivo sobre el marker de reset, o reclamar el slot de instancia única antes de resolver `MigrationService`
- [ ] **`CleanupUnresponsiveProcesses` (heredado de upstream) mata con SIGKILL** el PID anotado en el archivo de sync si el heartbeat tarda más de 6s. Desde el 2026-09-25 solo si el proceso sigue llamándose tModManager (un PID reusado ya no muere); un main propio ocupado todavía puede morir
- [ ] **Storage Manager: botones sin `CanExecute` atado a `IsBusy`** (solo guard dentro del cuerpo); el botón de cerrar ventana sigue activo mientras corre un paso del asistente
- [ ] **Deep Clean: `Directory.Move` falla entre filesystems** (librería de Steam en otro disco que `~/.local/share`) → esas carpetas quedan logueadas y sin mover; el `final.redscripts.bk` de redscript viejo se mueve pero un `final.redscripts` modeado se queda (Steam verify lo arregla)
- [ ] **Collections: `PackageReExtractionTests` reimplementa la secuencia restore-then-parse** en vez de correr `InstallCollectionJob` (hace falta un fixture de `CollectionRevisionMetadata` sin red)

### Otros TODO relevantes en código

- `NexusMods.Library/DownloadsService.cs:46` — restaurar descargas completadas desde storage al arrancar
- `NexusMods.Networking.NexusWebApi/NexusModsLibrary.Collections.cs:239-261` — metadata de colección hardcodeada (`AdultContent`, `Summary`, `Author`)
- `NexusMods.Networking.NexusWebApi/LoginManager.cs:303` — diálogo de "necesitás login" para operaciones
- `NexusMods.Backend/FileExtractor/Extractors/SevenZipExtractor.cs:251` — sin reporte de progreso
- `NexusMods.Games.RedEngine/RedModDeployTool.cs:89` — usa sort order del loadout en vez del "Active"
- `NexusMods.Games.RedEngine/Cyberpunk2077/SortOrder/RedMod/RedModSortOrderVariety.cs:87-267` — criterio de ganador por `ModGroupId` más reciente, mejorar
- `NexusMods.Abstractions.Games/SortOrder/ASortOrderVariety.cs:147,190` — sin retry ante data race en transacción
- `NexusMods.Games.RedEngine/Cyberpunk2077/Emitters/PatternBasedDependencyEmitter.cs:75` — usar index scan ordenado
- `NexusMods.App.UI/Settings/ExperimentalSettings.cs:19` — remover para GA
- `NexusMods.Backend/FileExtractor/FileExtractor.cs` (`ExtractAllAsync`) — traga la cancelación de cada intento de extractor y relanza `FileExtractionException` en vez de `OperationCanceledException`; el re-extractor lo esquiva con `ThrowIfCancellationRequested` explícito
