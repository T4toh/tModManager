# TODO

## 📍 Estado y próximos pasos (2026-09-22)

Mergeado hoy en `main`: limpieza (0 warnings), rename a **tModManager**, repo `T4toh/tModManager`, herencia upstream borrada, CI verde en ubuntu (1175 xUnit + 94 TUnit). Nada de esto se probó todavía con el juego: **primero probar en Linux, después seguir con desacoplar Cyberpunk del core.**

### Checklist de prueba en Linux

Antes de arrancar: backup de `~/.local/share/NexusMods.App.Cyberpunk/` (la migración hace `mv`, no copia).

1. **Build Release** (los `Debug.Assert` no corren en Release; si algo crashea acá es bug real):
   ```bash
   git pull && dotnet build -c Release
   dotnet run -c Release --project src/NexusMods.App/NexusMods.App.csproj
   ```
   Alternativa: AppImage con `./dev.sh` opción 10 (ya usa `-c Release`).
2. **Migración de data dir.** Al primer arranque stderr debe mostrar `Migrated data directory to .../tModManager`. Verificar:
   ```bash
   ls ~/.local/share/ | grep -i -e tModManager -e Cyberpunk   # solo tModManager
   ```
   La app tiene que abrir con el juego, loadouts y colecciones que ya tenías. Si aparece vacía, la migración falló: revisar stderr y el dir viejo.
3. **Handler nxm.** Después de que la app registre el handler (arranque normal):
   ```bash
   ls ~/.local/share/applications/ | grep -e cyberpunk -e tmodmanager   # solo io.github.t4toh.tmodmanager.desktop (+ .sh si el path necesita escape)
   xdg-mime query default x-scheme-handler/nxm                          # io.github.t4toh.tmodmanager.desktop
   ```
   Click en "Mod manager download" en Nexus → tiene que abrir tModManager, no el binario viejo.
4. **Sanity funcional.** Instalar un mod, Apply, lanzar el juego desde la app. Storage Manager abre. Descargar colección chica sin premium.
5. **Crashes esporádicos.** Si crashea en Release: log en `~/.local/share/NexusMods.App/Logs/nexusmods.app.main.current.log` (sí, todavía va al dir de la app oficial, ver deuda abajo). Pegar el stacktrace en la próxima sesión. Si solo crashea en Debug (`./dev.sh` opción 2) y el log dice `Assertion failed`, es un `Debug.Assert`: anotar cuál.
6. **Ventana.** Título y overlay de bienvenida dicen "tModManager". `StartupWMClass=tModManager` debería agrupar bien la ventana en el dock.

### Después de probar

- [ ] Reportar resultado del checklist (qué falló, logs).
- [ ] **Desacoplar Cyberpunk del core** (ver sección Multi-juego). Rama nueva desde `main`, con CI detrás.

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

- [x] **Loading indicators:** Agregado `IsLoading` al `APageViewModel` base con control `LoadingSection` reutilizable
- [ ] **Manejo de errores visible:** Muchos ViewModels tienen `// TODO: handle errors`. Implementar notificación al usuario vía `IWindowNotificationService` en todos los comandos async
- [ ] **Empty states consistentes:** El control `EmptyState` existe pero no todas las páginas lo usan. Auditar y completar: MyGames sin juego, Library vacía, Loadout sin mods
- [ ] **Accesibilidad básica:** Faltan `TabIndex`, estilos `:focus`/`:keyboard`, tooltips en botones icon-only
- [ ] **Strings hardcodeados:** Centralizar textos en español en `Language.resx` (actualmente mezclados inline en ViewModels y AXAML)
- [ ] **DesignViewModels rotos:** `ApplyDiffDesignViewModel` tira `NotImplementedException`; varios otros son stubs mínimos

## 🔮 Features Futuras

- [ ] **Eliminar .nx file store:** Reemplazar el sistema NxFileStore (herencia del modelo premium upstream) por acceso directo a los archivos descargados (.zip/.7z). Elimina la clase entera de bugs "archivos perdidos tras Deep Clean" y simplifica la validación a "¿existe el .zip?"
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
- [ ] **Vulnerabilidades NuGet (NU1901/2/3):** `Magick.NET-Q16-AnyCPU` 14.8.1 (vía `Verify.ImageMagick`, solo tests), `Tmds.DBus.Protocol` 0.21.2 (high, transitivo de Avalonia), `Microsoft.Build.Tasks.Git` 8.0.0 (vía `Microsoft.SourceLink.GitHub`). Actualizar respetando regla de supply chain (versiones con ≥7 días)

## 🎮 Multi-juego (después de limpieza)

Intento anterior falló por acoplamiento a Cyberpunk filtrado fuera de `Games.RedEngine` (~35 archivos). Orden:

- [x] **Renombrar app a tModManager:** app ID `io.github.t4toh.tmodmanager`, data dir `~/.local/share/tModManager/` con migración automática desde `NexusMods.App.Cyberpunk/`, `.desktop` viejo se borra al registrar el handler nxm. Pendiente: renombrar el repo GitHub `cp2077-mm` → `tModManager` (manual, GitHub redirige)
- [x] **CI propio:** GitHub Actions habilitado, `.github/workflows/ci.yaml` (ubuntu, `dotnet build -warnaserror`, xUnit vía `dotnet test` con filtro, TUnit vía `dotnet run`). Primera corrida verde: 1175 tests xUnit + 94 TUnit en ~4.5 min. Único arreglo necesario: ordenar hijos antes de `Verify` en `PathBasedInstallerTests` (orden de enumeración difiere entre ext4 y APFS)
- [ ] **Desacoplar Cyberpunk del core:** mover refs detrás de `IGame`. Sitios: `DataModel/Storage/StorageAnalyzer.cs`, `DataModel/DataModelSettings.cs`, `SchemaVersions/_0010_FixDeepCleanDisabledItems.cs`, `Sdk/FileExtractor/Signatures.cs`, `Abstractions.Games/SortOrder/*`, UI (`StorageManager`, `EssentialMods`, `MyGames`, `Welcome`, `ManualAddGame`, `GameWidget`), `SingleProcess/CliSettings.cs`, `App/Services.cs`, `App/Program.cs`
- [ ] **Recuperar `Games.CreationEngine` del history upstream** como base para Skyrim SE / Fallout 4 (mismo motor). Requiere: Proton, SKSE/F4SE, `plugins.txt` load order, FOMOD (ya existe)
- [ ] **Elegir primer juego:** Skyrim SE (más mods, más testeado) vs Fallout 4

## 🧬 Herencia de upstream a nivel repo

Hecho el 2026-09-22 (rama `feat/rename-tmodmanager`): borrados `.github/` completo (dependabot, issue templates, 17 workflows, scripts), submódulos `extern/SMAPI` y `docs/Nexus`, `docs/` + `mkdocs.yml`, `scripts/`, `codecov.yaml`, `qodana.yaml`, `CHANGELOG.md`, `CONTRIBUTING.md`, `NexusMods.App.sln.DotSettings`, `Nexus-Icon.png`, `.idea/` (untrackeado). `NuGet.Build.props` reducido a `GenerateDocumentationFile`. README con atribución a NexusMods.App (GPL-3.0). PR #25 de dependabot cerrado.

- [x] **Renombrar repo GitHub** `cp2077-mm` → `tModManager` (2026-09-22, GitHub redirige la URL vieja). URLs actualizadas en `metainfo.xml` y `app.pupnet.conf`
- [ ] **Issue templates propios** (bug + feature) si hace falta, cuando haya CI

## 🐛 Errores conocidos y deuda

Estado al 2026-09-22:

- **Issues abiertos en GitHub:** 0
- **Build:** 0 errores, 0 warnings de compilador (solo `NU19xx` de auditoría NuGet, ver arriba)
- **Comentarios `TODO`/`FIXME` en `src/`:** 99

### Otros TODO relevantes en código

- `NexusMods.Sdk/LoggingSettings.cs:135` — los logs van a `~/.local/share/NexusMods.App/Logs/` (dir de la app oficial) con nombre `nexusmods.app.*.log`. Mover a `tModManager/Logs/` usando `ApplicationConstants.DataDirectoryName`; cuidar que `dev.sh`/README apunten al path nuevo
- `NexusMods.Library/DownloadsService.cs:46` — restaurar descargas completadas desde storage al arrancar
- `NexusMods.Networking.NexusWebApi/NexusModsLibrary.Collections.cs:239-261` — metadata de colección hardcodeada (`AdultContent`, `Summary`, `Author`)
- `NexusMods.Networking.NexusWebApi/LoginManager.cs:303` — diálogo de "necesitás login" para operaciones
- `NexusMods.Backend/FileExtractor/Extractors/SevenZipExtractor.cs:251` — sin reporte de progreso
- `NexusMods.Games.RedEngine/RedModDeployTool.cs:89` — usa sort order del loadout en vez del "Active"
- `NexusMods.Games.RedEngine/Cyberpunk2077/SortOrder/RedMod/RedModSortOrderVariety.cs:87-267` — criterio de ganador por `ModGroupId` más reciente, mejorar
- `NexusMods.Abstractions.Games/SortOrder/ASortOrderVariety.cs:147,190` — sin retry ante data race en transacción
- `NexusMods.Games.RedEngine/Cyberpunk2077/Emitters/PatternBasedDependencyEmitter.cs:75` — usar index scan ordenado
- `NexusMods.App.UI/Settings/ExperimentalSettings.cs:19` — remover para GA
