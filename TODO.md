# TODO

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

## 🧹 Limpieza sistemática (en curso, rama `chore/systematic-cleanup`)

Objetivo: codebase confiable antes de tocar features. Un PR por bloque, build + tests entre cada uno.

- [ ] **Borrar proyectos vacíos/huérfanos del sln:** `NexusMods.Cli` (0 .cs), `NexusMods.UI` (0 .cs), `App.Generators.Diagnostics.Sample`, `src/Examples` (ejemplos upstream, nadie los referencia)
- [ ] **Warnings a cero:** `CS0105` usings duplicados en `Sdk/Loadouts/Models/Loadout.cs`, `CS0168` en `Sdk/Games/IGameData.cs`, `NU1510` `System.Linq` en `Abstractions.Loadouts.Synchronizers.csproj`, `CS0612 Tracking` en `CollectionCreator.cs`, `CS0618` `GameInstallMetadata.Name` / `ManuallyAddedGame` (obsolete upstream; quitar `[Obsolete]` o migrar)
- [ ] **Investigar `CS8785`:** generator `GenerateWeaveSources` (Fody) falla con `ArgumentNullException 'path2'`. Silencioso hoy, candidato a errores raros
- [ ] **`ExperimentalSettings`:** quitar `StardewValley` de `SupportedGames`, remover `EnableCollectionSharing` (TODO GA)
- [ ] **13 `// TODO: handle errors`:** cubrir con `IWindowNotificationService` o `ILogger` explícito (lista abajo)
- [ ] **Actualizar CLAUDE.md:** conteo de proyectos, stubs de telemetría ya no existen
- [ ] **Bugs runtime reales:** pendiente reproducir en Linux con juego instalado (crashes esporádicos reportados)

## 🎮 Multi-juego (después de limpieza)

Intento anterior falló por acoplamiento a Cyberpunk filtrado fuera de `Games.RedEngine` (~35 archivos). Orden:

- [ ] **Renombrar app:** "Cyberpunk 2077 Mod Manager" ya no aplica. Candidato: **tModManager**. Implica app ID (`com.cyberpunk2077.modmanager`), data dir, `.desktop`, `metainfo.xml`, `app.pupnet.conf`. Cuidar migración de data dir existente
- [ ] **Desacoplar Cyberpunk del core:** mover refs detrás de `IGame`. Sitios: `DataModel/Storage/StorageAnalyzer.cs`, `DataModel/DataModelSettings.cs`, `SchemaVersions/_0010_FixDeepCleanDisabledItems.cs`, `Sdk/FileExtractor/Signatures.cs`, `Abstractions.Games/SortOrder/*`, UI (`StorageManager`, `EssentialMods`, `MyGames`, `Welcome`, `ManualAddGame`, `GameWidget`), `SingleProcess/CliSettings.cs`, `App/Services.cs`, `App/Program.cs`
- [ ] **Recuperar `Games.CreationEngine` del history upstream** como base para Skyrim SE / Fallout 4 (mismo motor). Requiere: Proton, SKSE/F4SE, `plugins.txt` load order, FOMOD (ya existe)
- [ ] **Elegir primer juego:** Skyrim SE (más mods, más testeado) vs Fallout 4

## 🐛 Errores conocidos y deuda

Estado al 2026-09-22:

- **Issues abiertos en GitHub:** 0
- **Build:** 0 errores, 29 warnings (`CS0618` API obsoleta x32, `CS0105` using duplicado x8, `CS0612` x6, `NU1510` x4, `CS8785` x4, `CS1690` x2, `CS0168` x2)
- **Comentarios `TODO`/`FIXME` en `src/`:** 99

### `// TODO: handle errors` (fallos silenciosos, 13 sitios)

Ninguno notifica al usuario. Cubrir con `IWindowNotificationService` o logging explícito:

- `NexusMods.Networking.NexusWebApi/NexusModsLibrary.Collections.cs` — líneas 70, 98, 130, 151, 323
- `NexusMods.Networking.NexusWebApi/NexusModsLibrary.cs` — líneas 68, 88, 108
- `NexusMods.Networking.NexusWebApi/RunUpdateCheck.cs` — líneas 140, 149
- `NexusMods.Networking.NexusWebApi/NexusApiClient.cs` — línea 123
- `NexusMods.App.UI/Pages/LoadoutPage/LoadoutViewModel.cs` — línea 874
- `NexusMods.App.UI/Pages/CollectionDownload/CollectionDownloadViewModel.cs` — línea 576

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
