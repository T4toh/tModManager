# Mods locales de primera clase (pieza 3)

Fecha: 2026-10-10 · Estado: implementado en la rama `feat/local-mods-first-class` (PR #74), prueba en la app pendiente

## Objetivo

Que un mod agregado a mano quede igual de sólido que una descarga de Nexus: vive en
`tModManager/Downloads`, se reextrae si falta en el store, sobrevive a Deep Clean y al GC, y la
biblioteca muestra algo útil de él. Opcionalmente el usuario anota de dónde salió (fuente, URL,
versión). Es la pieza 3 de "Piezas genéricas para el segundo juego" en `TODO.md` y cierra el ítem
"Archivos locales fuera de Descargas". CP2077 es el caso de prueba (archivos agregados a mano);
Witcher 3 la usa para mods de mod.io/GitHub/foros y KOTOR para sus patchers.

### Qué hay hoy

- `LocalFile` (incluye `LibraryFile`) solo guarda `OriginalPath` (string absoluto). Sin fuente,
  URL ni versión.
- `AddLibraryFileJob` registra `LibraryFile.DownloadPath` solo si el archivo ya está dentro de
  `Downloads`. Un archivo elegido desde otra carpeta queda sin `DownloadPath`: `IDownloadReExtractor`
  no lo encuentra, el Storage Manager no lo cuenta, y si se borra del store el mod se pierde.
- Tres entradas: picker de la biblioteca ("agregar desde archivo", `LibraryViewModel`), overlay de
  descarga manual de colecciones (`ManualDownloadRequiredOverlay`) y el verbo CLI `loadout install`.
  El rescan de colecciones (`CollectionDownloader`) también llama `AddLocalFile`, siempre con archivos
  que ya están en `Downloads`.
- La columna Versión y "Ver página del mod" solo tienen datos para ítems de Nexus.
- `DownloadsFolder.PlaceAsync` ya coloca un archivo en `Downloads` con copia a temporal + `File.Move`
  atómico, reutiliza un archivo con el mismo nombre y contenido, y pone sufijo `_1`, `_2` cuando el
  nombre está tomado por otro contenido.

### Qué se decidió (2026-10-10)

- **Copiar, nunca mover.** El archivo del usuario no se toca. Costo: duplicado hasta que el usuario
  borre el suyo.
- **Metadata opcional, editable:** diálogo al agregar (se puede saltear con Enter) y "Editar
  metadata" desde la biblioteca después. Overlay de colecciones y CLI no muestran diálogo.
- **Atributos en `LocalFile`**, no un modelo compartido de "fuente". Cuando exista un segundo tipo de
  ítem no Nexus (mod.io, GitHub) se extrae lo común; hoy sería abstraer con un solo caso.
- **Sin chequeo de actualizaciones** para fuentes no Nexus, sin columna Fuente en el grid, sin
  renombrar grupos de loadout al editar el nombre en la biblioteca.

### Criterios de éxito

1. Un archivo elegido desde cualquier carpeta termina en `Downloads` con `DownloadPath`; borrarlo del
   store y aplicar lo repone desde `Downloads` sin pedir nada.
2. Un archivo elegido desde dentro de `Downloads` no se copia ni se duplica.
3. Elegir un archivo cuyo hash ya está en la biblioteca no crea un segundo ítem ni deja una copia.
4. Un origen que es symlink a archivo se copia como archivo regular; un symlink a directorio se
   rechaza con un error claro. Nunca se crea un link dentro de `Downloads`.
5. Los `LocalFile` viejos sin `DownloadPath` cuyo original sigue en disco se copian a `Downloads` una
   vez al arrancar; los que ya no tienen original quedan como están, sin excepción.
6. Nombre, versión, fuente y URL se guardan al agregar, se ven en la biblioteca (versión en su
   columna, URL en "Ver página del mod", fuente en el diálogo) y se editan después.
7. Nada del core asume Nexus: la fuente es texto libre y ningún campo es obligatorio.

## Diseño

### 1. Modelo de datos

`LocalFile` (`NexusMods.Sdk/Library/Models/LocalFile.cs`) suma tres atributos opcionales:

```csharp
public static readonly StringAttribute Version = new(Namespace, nameof(Version)) { IsOptional = true };
public static readonly StringAttribute Source = new(Namespace, nameof(Source)) { IsOptional = true };
public static readonly UriAttribute PageUri = new(Namespace, nameof(PageUri)) { IsOptional = true };
```

- `Version` y `Source` son texto libre. `PageUri` es la página del mod, no el link de descarga.
- El nombre para mostrar es `LibraryItem.Name`, que ya existe (hoy: nombre del archivo).
- `OriginalPath` queda como está: de dónde se copió, solo informativo.
- Atributos opcionales nuevos no requieren migración de schema.

### 2. Alta

Un record en `NexusMods.Abstractions.Library`:

```csharp
public record LocalFileMetadata(string? Name = null, string? Version = null, string? Source = null, Uri? PageUri = null)
{
    public static readonly LocalFileMetadata Empty = new();
}
```

`ILibraryService.AddLocalFile(AbsolutePath path, LocalFileMetadata? metadata = null)`. Los callers
actuales no cambian. `IAddLocalFile` expone `Metadata`.

`AddLocalFileJob.StartAsync`, en orden:

1. **Origen.** Si `FilePath` es un symlink a directorio, o no es un archivo, lanza
   `InvalidOperationException` nombrando la ruta. Un symlink a archivo sigue: `PlaceAsync` lee el
   contenido y escribe un archivo regular.
2. **Copia.** Si `FilePath.InFolder(DownloadsFolder)`, se usa tal cual. Si no,
   `DownloadsFolder.PlaceAsync(FilePath, DownloadsFolder, FilePath.FileName, ct)` devuelve la ruta
   final dentro de `Downloads` (reutilizada si ya había una igual, con sufijo `_N` si el nombre
   estaba tomado por otro contenido). `PlaceAsync` ya escribe a un temporal y mueve al final, así
   que un corte no deja un archivo a medias bajo un nombre final.
3. **Duplicado por hash.** Antes de `AddLibraryFileJob`, se hashea la copia (xxHash3) y se busca
   `LibraryFile.FindByHash`. Si hay un `LocalFile` con ese hash:
   - si tiene `DownloadPath` y ese archivo existe, se borra la copia recién hecha (solo si
     `PlaceAsync` la creó: distinta de la que ya tenía el `LocalFile`) y se devuelve el existente,
     aplicando la metadata nueva que venga no vacía;
   - si no tiene `DownloadPath` o su archivo ya no está, se conserva la copia y se le pone
     `DownloadPath` al existente (es el caso de los locales viejos, ver §4). Se devuelve el existente.
   El hash se calcula una vez más que hoy para el archivo top-level; aceptable (la extracción pesa
   mucho más). `AddLibraryFileJob` no cambia.
4. **Alta.** `AddLibraryFileJob.Create(tx, rutaFinal)` registra `DownloadPath` por el camino de
   siempre. `LocalFile.New` con `OriginalPath = FilePath` (la ruta elegida por el usuario) y los
   atributos de `Metadata` que no sean null ni vacíos. Si `Metadata.Name` viene, pisa
   `LibraryItem.Name`.

Nada cambia en `DownloadReExtractor`, `StorageAnalyzer`, `CollectionDownloader` ni en el rescan: todos
leen `DownloadPath`.

### 3. Diálogo de metadata (UI)

Overlay `LocalFileMetadataOverlay` en `NexusMods.App.UI/Overlays/LocalFileMetadata/` (mismo patrón
que `ManualAddGame`: `IXxxViewModel`, VM, `DesignViewModel`, vista AXAML, resultado como record).

- Campos: Nombre, Versión, Fuente, URL. Texto informativo: ruta de origen (al agregar) u
  `OriginalPath` (al editar).
- Prellenado al agregar: Nombre = nombre del archivo sin extensión; Versión = `LocalFileNameParser.TryParseVersion(fileName)`
  (regex sobre el nombre: `-1.2.3`, `_v1.2`, ` v1.2.3`, `1.2.3` antes de la extensión; devuelve null si
  no hay nada que parezca una versión). Fuente y URL vacías.
- Enter / "Agregar" acepta; Esc / "Cancelar" cancela el alta de ese archivo. URL inválida: el botón
  de aceptar se deshabilita y el campo lo marca; el resto no valida nada.
- Varios archivos seleccionados: un diálogo por archivo, en orden, en el hilo de UI; los jobs de alta
  corren a medida que se acepta cada uno (sin `Parallel.ForAsync` sobre los diálogos).
- `ManualDownloadRequiredOverlay` y el CLI no abren el diálogo. El verbo `loadout install` suma
  opciones `--version`, `--source`, `--url` opcionales y las pasa como `LocalFileMetadata`.

### 4. Biblioteca y edición

`LocalFileDataProvider`:
- Agrega `VersionComponent` en `LibraryColumns.ItemVersion.CurrentVersionComponentKey` cuando
  `Version` tiene valor (igual que `NexusModsDataProvider`).
- "Ver página del mod" habilitado cuando hay `PageUri`. `ViewModPageMessage` hoy es
  `OneOf<NexusModsModPageMetadataId, NexusModsLibraryItemId>`: suma un tercer caso `LocalFileId` y
  `LibraryViewModel.HandleViewModPageMessage` abre `PageUri` con `IOSInterop.OpenUrl`. Es la única
  rama Nexus-only que esta pieza toca; el resto del helper queda igual.
- Fuente no se muestra en el grid (`NameComponent` no tiene tooltip); solo en el diálogo.

Botón **"Editar"** en la barra de la biblioteca (la página no tiene menú contextual; todas las acciones por selección son botones), habilitado solo cuando la
selección es exactamente un `LocalFile` (se resuelve en `LibraryViewModel` con `TryGetAsLocalFile`).
Abre el mismo overlay con los valores actuales. Guardar escribe en una transacción solo lo que cambió;
un campo vaciado se retracta. El nombre editado cambia `LibraryItem.Name`; los grupos ya instalados
en loadouts conservan el nombre que tenían (hoy el grupo copia el nombre al instalar y queda
desacoplado; se mantiene).

### 5. Locales viejos fuera de `Downloads`

`LocalFileBackfill : IHostedService` en `NexusMods.Library`, registrado en `Services.cs` (mismo
patrón que `FileHashesService`). En `StartAsync`, en segundo plano:

1. Para cada `LocalFile` sin `LibraryFile.DownloadPath` cuyo `OriginalPath` existe en disco y es un
   archivo regular (no symlink a directorio): `PlaceAsync` a `Downloads` con el nombre original y
   `tx.Add(id, DownloadPath, rutaFinal.RelativeTo(Downloads))`.
2. Si el original no está: log de información con nombre y ruta, nada más.
3. Idempotente: salta todo lo que ya tiene `DownloadPath`. Una corrida que se corta se completa en el
   próximo arranque.

Los `LocalFile` que crea `ExternalDownloadJob` (descargas externas de colecciones) ya nacen en
`Downloads`, así que no entran.

### 6. Tests

En `tests/NexusMods.Library.Tests` (xUnit, `Startup.cs` con `AddDefaultServicesForTesting`, carpeta
de descargas temporal por test):

1. `AddLocalFile_OutsideDownloads_IsCopiedAndRegistered`: archivo en una carpeta temporal ajena →
   existe `Downloads/<nombre>`, `DownloadPath` seteado, el original sigue donde estaba, el store
   tiene el hash.
2. `AddLocalFile_InsideDownloads_IsNotCopied`: un solo archivo en `Downloads`, `DownloadPath` relativo.
3. `AddLocalFile_SameNameDifferentContent_GetsSuffix`: segundo archivo queda como `Mod_1.zip`.
4. `AddLocalFile_SameHashTwice_ReturnsExistingAndLeavesNoCopy`: dos `AddLocalFile` del mismo
   contenido desde rutas distintas → un solo `LocalFile`, un solo archivo en `Downloads`.
5. `AddLocalFile_SymlinkToFile_CopiesARegularFile` y `AddLocalFile_SymlinkToDirectory_IsRejected`:
   el primero verifica que `Downloads/<nombre>` no es symlink y que el destino del link no se tocó;
   el segundo verifica la excepción y que `Downloads` quedó vacío. Deben fallar sin el fix.
6. `AddLocalFile_WithMetadata_StoresEverything` y `AddLocalFile_EmptyMetadata_StoresNothing`.
7. `LocalFileNameParser` tabla: `Mod-1.2.3.zip` → `1.2.3`, `Mod_v1.2.zip` → `1.2`,
   `Mod v2.0.1 (fixed).7z` → `2.0.1`, `Mod-123-1-0-1757.zip` (nombre de Nexus) → null (no se adivina),
   `Mod.zip` → null.
8. `LocalFileBackfill_OriginalPresent_CopiesAndSetsDownloadPath` y
   `LocalFileBackfill_OriginalMissing_LeavesItemUntouched`.

En `tests/NexusMods.DataModel.Tests/DownloadReExtractorTests.cs`: un local agregado desde fuera de
`Downloads`, borrado del store, se repone desde la copia (criterio 1).

UI: `LocalFileMetadataOverlayDesignViewModel` para el previewer. Sin test de Avalonia.

### Fuera de alcance

Chequeo de actualizaciones para fuentes no Nexus; columna Fuente en el grid; mover en vez de copiar;
renombrar grupos de loadout al editar el nombre; modelo compartido de metadata de fuente (se extrae
cuando exista el segundo tipo de ítem no Nexus); información de juego en `LocalFile`
(`GetAllFiles(GameId)` sigue devolviendo vacío).
