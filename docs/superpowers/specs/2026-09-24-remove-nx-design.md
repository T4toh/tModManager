# Eliminar el `.nx` file store

Fecha: 2026-09-24 · Estado: implementado (PRs #42, #43 y PR 3 pendiente de abrir)

## Objetivo

Sacar el formato `.nx` (almacenamiento propio de Nexus) y todo lo que existe solo para mantenerlo, y
hacer que las descargas originales sean ciudadanas de primera clase: identificables, con el nombre que
les da Nexus, portables a otra PC o a otro manager (Vortex/MO2) y nunca borradas por sorpresa.

### Qué dijo Tatoh

- `.nx` es sobreingeniería: una ofuscación que le sirve a Nexus y complica todo sin ganar nada.
- El `.nx` no es lo que bajás de Nexus con el browser; varias veces quiso pasar sus mods a una PC con
  Windows y otro manager y no pudo.
- Arrancar de cero: es el único usuario. Sin migración de datos; la app avisa y guía la limpieza paso
  a paso con lo que ya existe (y limpiar más fuerte si hace falta).
- Carpeta de descargas propia en `tModManager/Downloads` (no más compartida con la app oficial).
- No reinventar: usar un patrón conocido.

### Criterios de éxito

1. No queda código ni paquetes de `.nx` (`NxFileStore`, `NexusMods.Archives.Nx`,
   `NexusMods.Paths.Extensions.Nx`, proyectos `GarbageCollection*`).
2. Instalar "Welcome to Night City" desde cero, aplicar y jugar funciona igual que hoy.
3. `tModManager/Downloads/` contiene los archivos con el nombre de Nexus y se puede copiar a otra
   máquina.
4. Borrar el store interno a mano no rompe nada: se reconstruye desde Descargas.
5. Deep Clean nunca borra Descargas.

### Fuera de alcance

- Convertir datos existentes (`.nx` → store nuevo).
- Borrar la descarga original después de importar (posible opción futura para ahorrar disco).
- Hardlinks/reflinks al aplicar (hoy se copia; se evalúa si el disco molesta).
- Reemplazar `NexusMods.Hashing.xxHash3` por `System.IO.Hashing` (va aparte, ver TODO).
- Desacoplar Cyberpunk del core (siguiente trabajo; este cambio le reduce la superficie).

## Contexto (estado actual)

Mapeado el 2026-09-24:

- `IFileStore` (`src/NexusMods.Sdk/FileStore/IFileStore.cs`) tiene 7 métodos: `HaveFile`,
  `BackupFiles`, dos `ExtractFiles`, `GetFileStream`, `Load`, `WriteLock`, `ReloadCaches`.
  Una sola implementación real: `NxFileStore` (659 líneas). 16 consumidores de producción.
- `.nx` no tiene huella en la base: `NxFileStore` indexa escaneando `*.nx` en disco. Ninguna migración
  de schema lo toca.
- Flujo de descarga: temp → copia a `NexusMods.App/Downloads/<nombre>` (ruta hardcodeada en
  `AddDownloadJob.cs:206` y `CollectionDownloader.cs:919`, además del setting) → se descomprime todo →
  las hojas no-archivo se empaquetan en un `<guid>.nx`. La ruta de la copia **no** queda en la base; los
  nombres repetidos llevan `_1`, `_2`.
- Aplicar: `ALoadoutSynchronizer.ActionExtractToDisk` → `IFileStore.ExtractFiles(hash → destino)`.
  `HaveFile` se llama sincrónicamente (`.Result`) tres veces por nodo en `ProcessSyncTree` y afecta
  qué acciones se toman.
- Viven en `.nx` sin tener zip de origen: backups de archivos del juego (`GameBackedUpFile`), salida
  de parches de colecciones (BsDiff), guardados del editor de texto, archivos de
  `ManuallyCreatedArchive`, descargas sueltas que no son archivo comprimido.
- GC: tres proyectos (`App.GarbageCollection`, `.Nx`, `.DataModel`) que reempaquetan `.nx`; se dispara
  después de cada Apply, al borrar items de la librería, al des-gestionar y desde Storage Manager.
- Bugs existentes que este cambio resuelve:
  - `TryReExtractMissingFiles` (`InstallCollectionDownloadJob.cs:485`) busca en `.nx` el archivo padre,
    que nunca se guarda ahí: la reextracción nunca funciona.
  - `InstallCollectionJob.cs:106` busca el paquete de la colección en `.nx`: siempre lo vuelve a bajar.
  - `StorageAnalyzer.DeleteArchivesAsync` borra los `.nx` sin `ReloadCaches`: índice desactualizado
    (origen probable de los "archivos perdidos tras Deep Clean").
  - `StorageAnalyzer.DeletePhysicalFilesAsync` borra toda la carpeta de Descargas en cada Deep Clean de
    la UI, y esa carpeta es compartida con la app oficial.
  - `CyberpunkBackups` apunta a `NexusMods.App/`, no a `tModManager/`.

## Diseño

### 1. Store interno: archivos sueltos por hash

Patrón de `.git/objects`, pnpm y Nix: cada archivo se guarda una vez, con su hash como nombre.

- Nueva clase `LooseFileStore : IFileStore` en `NexusMods.DataModel`, registrada en lugar de
  `NxFileStore`.
- Ubicación: `~/.local/share/tModManager/DataModel/Archives/`. `DataModelSettings.ArchiveLocations`
  se conserva (lo usa la UI de settings) pero solo se usa el primer elemento.
- Layout: `Archives/<2 primeros hex>/<hash hex completo>`, hash xxHash3 como hoy. El prefijo evita
  carpetas con cientos de miles de entradas.
- `BackupFiles`: escribe a `<destino>.tmp-<guid>` en la misma carpeta y hace `File.Move` atómico al
  nombre final. Si el hash ya existe, se saltea (dedupe). Un corte a mitad nunca deja un archivo con
  nombre de hash válido y contenido incompleto.
- `HaveFile(hash)`: `File.Exists`. Sincrónico y barato, compatible con el uso de `ProcessSyncTree`.
- `ExtractFiles((hash, destino)[])`: copia en paralelo; crea directorios destino; los archivos de
  tamaño 0 se crean directo sin leer el store. Hash faltante → `MissingArchiveException` (se conserva
  el tipo actual) con la lista de hashes.
- `ExtractFiles(hashes) → Dictionary<Hash, byte[]>`, `GetFileStream`, `Load`: lectura directa.
- `WriteLock()` y `ReloadCaches()` salen de `IFileStore`: no hay caché ni reempaquetado concurrente.
- El store no sabe nada de la librería: la reextracción desde Descargas es un servicio aparte (§2).

GC: se mantiene la interfaz `IGarbageCollectorRunner` (19 usos) para no tocar a quien lo dispara.
Implementación nueva, un barrido:

1. Junta los hashes vivos con la misma lógica que hoy tiene `DataStoreReferenceMarker`:
   `LoadoutFile.Hash` de loadouts válidos, hashes de `LibraryArchiveFileEntry` y de `GameBackedUpFile`.
   Se agregan también los `LibraryFile` de primer nivel que no son archivo comprimido (hoy no se
   marcan: bug latente).
2. Borra del store todo archivo cuyo hash no esté en ese conjunto.

Se borran: `NxFileStore.cs`, los proyectos `NexusMods.App.GarbageCollection`, `.Nx`, `.DataModel` y sus
tres proyectos de tests, los paquetes `NexusMods.Archives.Nx` y `NexusMods.Paths.Extensions.Nx`, y el
código muerto sin consumidores (`IStreamSourceDispatcher`, `StreamSourceDispatcher`,
`GameFileStreamSource`, `SourceDispatcherStreamFactory`, `FileStoreStreamLoader`).
`Abstractions.GC` queda con la interfaz y el enum de modo.

### 2. Descargas de primera clase

- Una sola fuente de la ruta: `DownloadsSettings.Folder` (nuevo, en `NexusMods.Sdk` para que lo
  vean Library, Collections y DataModel), default `~/.local/share/tModManager/Downloads`. Sale
  `DataModelSettings.DownloadsFolder`. Se eliminan las copias hardcodeadas de `AddDownloadJob` y
  `CollectionDownloader`; ambos leen el setting.
- Nombre del archivo: el que da Nexus (el mismo nombre que baja el browser; hoy ya se calcula un
  "nombre significativo" en `AddDownloadJob.PreserveOriginalFile`, se reutiliza esa fuente).
  Si el nombre ya existe:
  - mismo hash → se reutiliza el existente, no se copia;
  - distinto contenido → sufijo `_1`, `_2` (no se pisa nada).
- Atributo nuevo `LibraryFile.DownloadPath` (`RelativePathAttribute`, opcional), relativo a
  `DownloadsFolder`, para que mover la carpeta entera siga funcionando. Sin migración: arrancamos de
  cero.
- Reextracción: servicio `DownloadReExtractor` (en `NexusMods.Library`). Dado un conjunto de hashes
  faltantes, busca los `LibraryArchiveFileEntry` con esos hashes, sube hasta el `LibraryFile` de primer
  nivel con `DownloadPath` existente en disco, descomprime ese archivo (con anidados) y carga en el
  store solo las hojas faltantes. `HaveFile` sigue siendo solo `File.Exists` (barato, sin efectos).
  Se invoca en dos lugares:
  - `ALoadoutSynchronizer.ActionExtractToDisk`, antes de `ExtractFiles`: calcula los hashes que faltan
    y los pide; lo que siga faltando termina en `MissingArchiveException` como hoy;
  - validación de colecciones, reemplazando `TryReExtractMissingFiles` (que hoy nunca funciona).
- Paquete de colección: la validación de `InstallCollectionJob` mira el archivo en Descargas, no el
  store. Deja de bajarse en cada instalación.
- Lo que no tiene zip (backups del juego, parches, ediciones, `ManuallyCreatedArchive`, archivos
  sueltos) vive solo en el store. Si se borra el store, se pierde; los backups se regeneran al volver a
  gestionar el juego.
- `StorageAnalyzer.DeletePhysicalFilesAsync` deja de borrar Descargas y deja de formar parte del Deep
  Clean. Storage Manager gana un botón aparte "Borrar descargas" con confirmación explícita.
- MD5 rescan (`CollectionDownloader.RescanDownloads`) busca en la carpeta nueva; al reconocer un
  archivo le asigna `DownloadPath`.

### 3. Detección de datos viejos y limpieza guiada

Detección: al arrancar, si existe algún `*.nx` en la ubicación de archivos, la app abre la base
normalmente (MnemonicDB no cambia) pero bloquea la navegación con un overlay (sistema de overlays existente, como el de bienvenida) con 4 pasos:

1. **Aviso.** tModManager ya no usa `.nx`; hay que empezar de cero; se borran librería y loadouts; las
   descargas se conservan. Seguir o salir.
2. **Rescatar descargas.** Busca archivos en `NexusMods.App/Downloads`, muestra cantidad y tamaño y los
   mueve a `tModManager/Downloads` (mover, no copiar). Si el destino ya tiene un archivo con el mismo
   nombre y hash, se descarta el origen; con distinto hash se aplica la regla de sufijo.
3. **Limpiar la carpeta del juego.** Deep Clean reforzado (ver abajo), backup en
   `tModManager/Backups/<timestamp>/`. Después:
   - botón "Verificar integridad en Steam" que abre `steam://validate/1091500`;
   - opcional "Borrar prefix de Proton" (`steamapps/compatdata/1091500/`), con advertencia de saves no
     sincronizados con la nube y configuración del prefix.
4. **Borrar datos viejos.** Borra `MnemonicDB.rocksdb` y los `*.nx`; reinicia la app.

El paso 3 va antes del 4 porque el Deep Clean usa la base para saber qué grupos de mods había.

Deep Clean reforzado (`CyberpunkDeepCleanTool`), útil también fuera del asistente. Agrega:

- raíz del juego: todo archivo que no sea vanilla (readmes, `.txt/.png/.jpg` de mods,
  `launcher-configuration.json`, `REDprelauncher.exe`, `REDlauncher-*.msi`, `*.dll`); la lista vanilla
  sale de `Games.FileHashes` para la versión detectada;
- `r6/audioware/`, `r6/input/`, `r6/config/cybercmd/`, `r6/config/redsUserHints/`, `r6/publishing/`,
  `r6/logs/`;
- `engine/config/platform/pc/*.ini` que no sean vanilla, `engine/config/base/scripts.ini` si no es
  vanilla, `r6/cache/final.redscripts*`, `r6/cache/input*.xml`, `tools/redmod/tweaks/**/devices.tweak`
  si no es vanilla, `bin/x64/CyberPunk.bat`.

Los backups de Deep Clean pasan de `NexusMods.App/CyberpunkBackups` a `tModManager/Backups`.
El botón de prefix de Proton y el Deep Clean reforzado también quedan sueltos en Storage Manager.

### 4. Pruebas

Automáticas:

- `LooseFileStore`: escritura, dedupe, escritura interrumpida sin archivo final, `HaveFile`,
  extracción con archivos vacíos, hash faltante con `MissingArchiveException`. Reemplaza
  `FileStoreTests`.
- GC: se conserva lo referenciado (loadout, librería, backups, archivos sueltos de primer nivel) y se
  borra lo demás. Reemplaza los tres proyectos de tests del GC.
- Descargas: reutilización por mismo nombre y hash, sufijo por distinto contenido, `DownloadPath`
  guardado, reextracción desde Descargas cuando falta un hash.
- Detección: con `*.nx` se dispara el asistente, sin `*.nx` no.
- Deep Clean: un caso por ruta nueva.
- Los tests del Synchronizer y los installers no cambian: son la verificación de que la interfaz se
  mantuvo. Se adaptan los helpers de `AGameTest`/`AIsolatedGameTest` que usan `NxFileStore`.
- Suite completa en container Linux comparada contra `main`, más CI.

Punta a punta en el Linux de Tatoh (checklist en el PR final):

1. Backup de saves.
2. Abrir la app: asistente → rescatar descargas → limpiar juego → verificar en Steam → borrar datos →
   reinicio.
3. Gestionar el juego, instalar "Welcome to Night City"; anotar cuántos mods reutiliza el MD5 rescan y
   cuántos baja.
4. Aplicar y lanzar el juego: Redscript, CET y RED4ext sin errores.
5. Disco: ningún `.nx`; existe `Archives/ab/...`; `Downloads/` con nombres de Nexus.
6. Borrar `Archives/` a mano y aplicar de nuevo: todo se reextrae desde Descargas.
7. Deep Clean: `Downloads/` intacta.

## Entrega

Tres PRs apilados, un solo release después de la prueba punta a punta:

1. `LooseFileStore` + GC por barrido + eliminación de `.nx`, GC viejo, paquetes y código muerto.
2. Descargas de primera clase (§2).
3. Asistente de datos viejos + Deep Clean reforzado + botón de prefix de Proton (§3).

El PR 1 solo no lee datos viejos; no se publica nada hasta tener los tres y la prueba en verde.
Al cerrar: actualizar `CLAUDE.md` (Fork-Specific Features 2, 3, 5 y 7; test framework) y `TODO.md`
(sección de dependencias de Nexus y errores conocidos de Deep Clean).

## Cambios respecto del diseño

Lo que quedó implementado difiere del diseño original en estos puntos:

- **`r6/publishing` no se mueve entero.** El diseño lo listaba junto a `r6/audioware`, `r6/input`, etc.
  como carpeta para mover wholesale. En la práctica el depot base de Steam ya publica archivos vanilla
  ahí (`r6/publishing/*/*/additional-content/addonDescriptions.xml`, confirmado contra el manifiesto del
  depot), así que `CyberpunkDeepCleanTool` lo excluyó de `PathsToMove` y lo agregó a `LooseFileGlobs`:
  se diffa archivo por archivo contra el set vanilla de `IFileHashesService`, igual que la raíz del
  juego, en vez de moverse entero.
- **`LooseFileStore` tiene un período de gracia de 1 hora para el GC.** El diseño describía un barrido
  simple (vivo vs. no vivo). La implementación agrega una ventana de 1h antes de borrar un archivo sin
  referencia (o un temporal `.tmp-*` huérfano), para no correr contra un backup recién escrito cuyo
  commit a la base todavía no aterrizó. El GC solo toca su propio layout (`XX/<hash>` de dos hex más
  16 hex), nunca archivos o subcarpetas ajenas bajo `Archives/`.
- **El asistente corre Deep Clean sin los syncs de aplicar/ingerir**, no el `RunDeepCleanOnAllLoadoutsAsync`
  normal: esos syncs necesitarían leer los `.nx` viejos, que ya no se pueden abrir. `IStorageAnalyzer`
  expone `RunDeepCleanWithoutSyncOnAllLoadoutsAsync` para este caso, usado solo desde el asistente.
- **El paso de limpieza del asistente tiene una salida "Continuar sin limpiar".** Si el Deep Clean o el
  borrado del prefix de Proton fallan, el usuario puede saltear ese paso y seguir al reseteo/reinicio en
  vez de quedar trabado; el asistente registra que la limpieza se saltó.
- **El reinicio espera a que el proceso viejo salga** antes de ejecutar el nuevo (`AppRestart.RestartAfterExit`):
  un script de shell hace polling sobre el PID viejo (hasta ~60s) y recién ahí hace `exec` del ejecutable
  nuevo, para que la base de datos nunca se abra desde dos procesos a la vez. Si el proceso viejo no
  termina a tiempo, el script se rinde en vez de arrancar una segunda instancia en carrera; el marker de
  reset queda puesto para el próximo arranque manual.
- **El reset al arrancar se saltea si otra instancia principal podría seguir viva.** `Program.cs` corre
  `CleanupUnresponsiveProcesses` antes de decidir si actuar como main; si esa comprobación no concluye
  positivamente que no hay otra instancia (falla, da timeout, o encuentra una viva), este proceso nunca
  resuelve `MigrationService` ni toca el reset pendiente, y en cambio reenvía los argumentos como una
  segunda instancia. Evita que dos lanzamientos dentro de la ventana de migración borren la base el uno
  al otro.
