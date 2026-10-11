# Archivos intrínsecos con entradas por mod e `Ingest` (pieza 4)

Fecha: 2026-10-10 · Estado: implementado en la rama `feat/intrinsic-settings-file` (PR #NN), prueba con el juego pendiente

## Objetivo

Que un juego pueda declarar un archivo de configuración que **el juego reescribe** (orden de carga,
settings) y que el loadout posea algunas de sus claves sin pisar las del juego ni las del usuario.
Es la pieza 4 de "Piezas genéricas para el segundo juego" en `TODO.md`. Witcher 3 la necesita para
`mods.settings` y `dx12user.settings`/`input.settings`; Skyrim para `Plugins.txt`. CP2077 es el
caso de prueba con `UserSettings.json`: ningún mod lo toca hoy, pero es el archivo que el juego
reescribe al salir, que es justo la parte difícil (`Ingest`).

### Qué hay hoy

- `IIntrinsicFile` (`Abstractions.Loadouts.Synchronizers`) existe y está cableado: la función de
  tabla `IntrinsicFiles` (`DataModel/Synchronizer/DbFunctions`) mete los intrínsecos de cada
  loadout en el sync tree como `LoadoutSourceItemType.Intrinsic`; `ActionMapping` manda
  `WriteIntrinsic` cuando el archivo falta en disco o el disco está igual que la última
  sincronización, y `AdaptLoadout` → `Ingest` cuando el disco cambió. No se respaldan ni se tratan
  como mods. `ALoadoutSynchronizer.IntrinsicFiles(loadout)` es virtual y devuelve vacío.
- **Ningún juego lo usa.** El `PluginsFile` de upstream (`Games.CreationEngine`, borrado) escribía
  el load order y tenía `Ingest` vacío.
- `AdaptLoadout` solo lee: después de un `Ingest` el archivo no se reescribe hasta el apply siguiente.
- `ActionWriteIntrinsics` abre el archivo con `Create()` + `SetLength(0)` antes de llamar a `Write`:
  la implementación no puede leer el contenido anterior desde ahí.
- REDmod recibe su `modlist` como archivo temporal al desplegar (`RedModDeployTool`); no es un
  intrínseco del juego. Eso es pieza 5.
- `UserSettings.json` de CP2077 ya es un archivo gestionado por whitelist en `LocationId.WinePrefix`
  (pieza 2): se adopta como original, se respalda, y reset / dejar de gestionar lo restauran.
- Formato real del archivo (`version: 140`):
  `{ "version": 140, "data": [ { "group_name": "...", "options": [ { "name", "type", "value", "default_value", "min_value", "max_value", "step_value" } ] } ] }`.

### Qué se decidió (2026-10-10)

- **A: motor genérico + primer uso real en CP2077** con `UserSettings.json` y un verbo CLI para
  cargar entradas, para probar con el juego real que al salir reescriba el archivo y nuestra clave
  sobreviva. Sin UI nueva. (Alternativas descartadas: probar solo con el juego stub; esperar a W3.)
- **El juego cambia una clave que poseemos → External Change:** `Ingest` crea una entrada en el
  grupo de External Changes con el valor del juego, que gana sobre los mods, igual que hoy con
  cualquier archivo que el juego cambia. El usuario la ve y la borra si quiere volver al valor del
  mod. Las claves que nadie posee solo actualizan la base. (Descartado: el loadout reimpone en
  silencio; el juego sobrescribe la entrada del mod.)
- **La base se guarda como snapshot en el loadout** en cada `Ingest`, así `Write` es puro
  (loadout → bytes) y si el juego borra el archivo se regenera entero.
- **Después de un `Ingest` el core reescribe en el mismo apply** si lo generado difiere del disco.
- **Solo el formato JSON de CP2077 ahora.** INI por secciones (W3, Skyrim) y lista de líneas
  (`Plugins.txt`, `modlist`) llegan con el juego que los pida; la clase base deja el lugar.
- Nada del core conoce CP2077 ni Nexus: el core aporta el modelo y la clase base; el formato y la
  ruta viven en `Games.RedEngine`.

### Criterios de éxito

1. Una entrada en un mod habilitado aparece en `UserSettings.json` tras el apply; el resto del
   archivo queda como estaba.
2. Si el juego cambia claves que no poseemos o agrega nuevas, el apply siguiente las conserva (base
   nueva), mantiene nuestras claves y reescribe el archivo en ese mismo apply.
3. Si el juego cambia una clave que poseemos, aparece una entrada en External Changes con el valor
   del juego y ese valor queda en disco; borrarla y aplicar devuelve el valor del mod.
4. Si el juego borra el archivo, el apply lo regenera desde la base y las entradas.
5. Dejar de gestionar con limpieza restaura el `UserSettings.json` original respaldado (pieza 2).
6. Si `UserSettings.json` es un symlink a un archivo externo, ni `Write` ni la escritura posterior a
   `Ingest` lo tocan; el archivo externo no cambia. El test falla sin el guard.
7. Sin prefix declarado, `IntrinsicFiles` está vacío y los snapshots `.verified.` existentes no cambian.
8. `SynchronizerRuleTests.RulesAreAsExpected.verified.txt` no cambia: no se toca `ActionMapping`.

## Diseño

### 1. Modelo de datos (`NexusMods.Abstractions.Loadouts`)

```csharp
[Include<LoadoutItem>]
public partial class IntrinsicFileEntry : IModelDefinition
{
    private const string Namespace = "NexusMods.Loadouts.IntrinsicFileEntry";
    /// <summary>The intrinsic file this entry belongs to.</summary>
    public static readonly GamePathAttribute File = new(Namespace, nameof(File)) { IsIndexed = true };
    /// <summary>Key as the file's format understands it (CP2077: "group_name/name").</summary>
    public static readonly StringAttribute Key = new(Namespace, nameof(Key));
    /// <summary>Value as a literal of the file's format (CP2077: a JSON literal).</summary>
    public static readonly StringAttribute Value = new(Namespace, nameof(Value));
}

public partial class IntrinsicFileState : IModelDefinition
{
    private const string Namespace = "NexusMods.Loadouts.IntrinsicFileState";
    public static readonly ReferenceAttribute<Loadout> Loadout = new(Namespace, nameof(Loadout)) { IsIndexed = true };
    public static readonly GamePathAttribute File = new(Namespace, nameof(File));
    /// <summary>Text of the file as last ingested from disk: everything the loadout does not own.</summary>
    public static readonly StringAttribute BaseContent = new(Namespace, nameof(BaseContent));
    public static readonly HashAttribute IngestedHash = new(Namespace, nameof(IngestedHash));
}
```

- `IntrinsicFileEntry` incluye `LoadoutItem`, **no** `LoadoutItemWithTargetPath`: el query de
  archivos ganadores (`WinningFilesQuery`) solo mira ítems con `TargetPath`, así una entrada nunca
  es "un archivo" en el sync tree ni choca con el nodo intrínseco.
- Una entrada vive en cualquier grupo: un mod, una colección, el grupo de External Changes o el
  grupo "Ajustes" del loadout (§5). Se respeta `Disabled` del ítem y de sus padres como con los
  archivos.
- Si `GamePathAttribute` no existe en MnemonicDB/Sdk, se guarda `File` como `StringAttribute` con
  `GamePath.ToString()` y se parsea al leer (`LocationId.From` + `RelativePath`).
- **Ganador por clave:** entre las entradas habilitadas para la misma clave gana la que está en el
  grupo de External Changes; si no hay, la de `EntityId` mayor (determinista: la agregada más
  tarde). Orden por load order entre mods queda para la pieza 5.

### 2. `ASettingsIntrinsicFile<TDoc>` (`NexusMods.Abstractions.Loadouts.Synchronizers`)

Clase base abstracta que implementa `IIntrinsicFile`. El juego solo define el formato:

```csharp
public abstract class ASettingsIntrinsicFile<TDoc> : IIntrinsicFile
{
    public GamePath Path { get; }
    protected abstract TDoc Parse(string text);           // texto vacío → documento vacío válido
    protected abstract string Serialize(TDoc document);
    protected abstract bool TryGet(TDoc document, string key, out string value);
    protected abstract void Set(TDoc document, string key, string value);

    public Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree);
    public Task<ReadOnlyMemory<byte>?> Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx);
}
```

`IIntrinsicFile.Ingest` pasa a devolver `ReadOnlyMemory<byte>?`: el contenido que debe quedar en
disco después de ingerir, o `null` si no hay nada que reescribir. Es un cambio de interfaz sin otros
implementadores. Así el core no necesita leer datoms sin commitear ni la clase base guardar estado
entre llamadas: `Ingest` ya conoce la base nueva y las entradas que acaba de crear.

- `Write`: `Parse(BaseContent del IntrinsicFileState o "")` → `Set` de cada entrada ganadora
  (`WinningEntries(loadout)`: entradas de ítems habilitados con `File == Path`, resueltas por §1) →
  `Serialize` al stream, UTF-8 sin BOM.
- `Ingest`: lee el texto del disco; guarda `BaseContent = texto`, `IngestedHash = hash del disco`
  (crea el `IntrinsicFileState` si no existe); `Parse(texto)` y, para cada entrada ganadora cuya
  clave tenga en disco un valor distinto (`TryGet`) al esperado, crea o actualiza una
  `IntrinsicFileEntry` con el valor del disco en el grupo de External Changes
  (`GetOrCreateOverridesGroup`, el mismo que usan los archivos). Si la clave ya tenía una entrada
  en External Changes con ese valor, no hace nada. Claves que nadie posee: no generan entradas.
  Devuelve el render de `base nueva + entradas ganadoras` (con las External Changes recién creadas
  aplicadas en memoria) como bytes, o `null` si es idéntico al disco.
- `Write` toma la base del snapshot y no del disco a propósito: `ActionWriteIntrinsics` ya truncó el
  archivo cuando llama a `Write`, y un archivo que el juego borró se regenera igual.
- Primer apply sin snapshot y sin archivo en disco: base vacía, `Write` escribe solo las entradas
  (CP2077: `{ "version": ?, "data": [...] }` con la versión que diga el formato; el juego completa
  el resto al arrancar). Con archivo en disco y sin snapshot, la regla es `AdaptLoadout` →
  `Ingest` primero, así la base real entra antes del primer `Write`.

### 3. Cambios al synchronizer (`ALoadoutSynchronizer`)

- `AdaptLoadout`: si `instance.Ingest(...)` devuelve bytes, los escribe en el archivo (`Create` +
  copia) en el mismo apply. Así nuestras claves aterrizan de inmediato y el disco queda igual al
  loadout. El estado de disco que se graba al final del sync toma el hash de lo escrito.
- `EnsureDiskChangesStayInside` suma `Actions.AdaptLoadout` a `diskChanges`: la escritura posterior
  a `Ingest` pasa por las mismas guardas de whitelist y symlinks que `WriteIntrinsic`.
- `Cyberpunk2077Synchronizer.IntrinsicFiles(loadout)`: si la instalación declara
  `LocationId.WinePrefix`, devuelve `{ ruta de UserSettings.json → UserSettingsFile }`; la ruta sale
  del mismo helper que `GetManagedFiles` (se extrae a un método compartido en `Cyberpunk2077Game`).
  Sin prefix, vacío.
- Pieza 2 intacta: el archivo sigue en la whitelist y en la lista vanilla (`GameBaselineFile`), así
  que reset y dejar de gestionar restauran el original respaldado; que el nodo sea intrínseco en el
  sync tree no cambia `ResetToOriginalGameState` (criterio 5, con test).
- `ActionMapping` no se toca (criterio 8).

### 4. CP2077: `UserSettingsFile` (`NexusMods.Games.RedEngine/Cyberpunk2077/UserSettingsFile.cs`)

`UserSettingsFile : ASettingsIntrinsicFile<JsonNode>`:
- `Parse`: `JsonNode.Parse(texto)`; texto vacío → `{ "version": 140, "data": [] }`.
- Clave `"<group_name>/<name>"`: busca en `data[]` el grupo con `group_name` igual y dentro de
  `options[]` la opción con `name` igual; `TryGet` devuelve `value` serializado como literal JSON
  (`5.0`, `true`, `"Off"`); `Set` parsea el valor con `JsonNode.Parse` y reemplaza solo `value`.
  `type`, `default_value`, mínimos y cualquier campo desconocido quedan intactos.
- Clave inexistente en `Set`: agrega `{ "name": ..., "value": ... }` al grupo, creando el grupo
  (`{ "group_name", "options": [] }`) si falta, y loguea en Info. No inventa `type`.
- Valor que no es un literal JSON válido: `Set` lanza `InvalidOperationException` nombrando la
  clave; el verbo CLI lo valida antes de guardar y el apply falla con mensaje claro si llegó por
  otra vía.
- `Serialize`: `WriteIndented = true`; el juego no depende del formato.

### 5. CLI (`DataModel/CommandLine/Verbs/LoadoutManagementVerbs.cs`)

- `loadout settings set -l <loadout> -k <clave> -v <valor> [-f <ruta>]`: crea o actualiza la
  `IntrinsicFileEntry` con esa clave en el grupo "Ajustes" del loadout (`LoadoutItemGroup` creado a
  demanda, nombre fijo `Ajustes`, hijo directo del loadout). `-f` es el `GamePath` del intrínseco
  (`WinePrefix:drive_c/...`); si se omite y el juego declara un solo intrínseco, se usa ese; si
  declara varios o ninguno, error. Valida el valor con `Set` sobre un documento vacío.
- `loadout settings list -l <loadout>`: por archivo, cada entrada con clave, valor y grupo dueño
  (nombre del mod, `Ajustes` o External Changes), marcando la ganadora cuando hay repetidas.
- Sin UI nueva en esta pieza.

### 6. Tests

En `tests/NexusMods.DataModel.Synchronizer.Tests`, clase nueva `IntrinsicSettingsFileTests` con
`WithWinePrefix => true` y el `UserSettings.json` de ejemplo (recortado del real, dos grupos, tres
opciones) escrito en el prefix antes de gestionar:

1. `EntryInAMod_IsWrittenAndTheRestIsKept` (criterio 1).
2. `GameChangesForeignKeys_BaseUpdatesAndOurKeyStaysInTheSameApply` (criterio 2): edita una clave
   ajena y agrega un grupo nuevo, aplica una vez, verifica el archivo.
3. `GameChangesOurKey_BecomesAnExternalChangeThatWins` y
   `DeletingTheExternalChange_RestoresTheModValue` (criterio 3).
4. `GameDeletesTheFile_ItIsRegenerated` (criterio 4).
5. `UnmanageWithCleanup_RestoresTheBackedUpOriginal` (criterio 5).
6. `SettingsFileThatIsASymlink_IsNeverWritten` (criterio 6): link a un archivo externo con canario;
   la entrada en un mod y una edición "del juego" del externo; apply rechaza o no escribe; el
   externo queda igual. Debe fallar sin el guard en `AdaptLoadout`.
7. Los proyectos `Synchronizer.Tests` y `RedEngine.Tests` enteros sin cambios en `.verified.`
   (criterio 7) y `SynchronizerRuleTests` igual (criterio 8).

En `tests/Games/NexusMods.Games.RedEngine.Tests`, `UserSettingsFileTests`: parsea el JSON de ejemplo
y preserva `version`, campos desconocidos y tipos; `TryGet` de clave existente e inexistente; `Set`
sobre existente reemplaza solo `value`; `Set` sobre inexistente agrega sin romper; valor inválido
lanza; texto vacío produce el documento mínimo.

### Ajustes surgidos en la implementación (2026-10-10)

- **Siempre se ingiere antes de escribir.** `WriteIntrinsic` también pasa por `Ingest` (con un stream
  vacío si el archivo no existe): el primer apply después de gestionar ve disco == estado previo y un
  `Write` ciego desde base vacía borraba las claves del juego. `Write` queda para quien genere archivos
  sin base.
- **"Cambio del juego"** = el valor en disco difiere de la entrada **y** del valor de la base anterior
  para esa clave. Sin base previa (primera vez) el disco es el original y la entrada aplica.
- **Archivo ausente** → se regenera desde la base guardada (nunca se reemplaza la base por vacío).
  **Archivo que no parsea** → con base previa se repara desde ella; sin base se deja en paz. **Sin
  archivo, sin base y sin entradas** → no se escribe nada (no se inventa un archivo para un juego que
  nunca corrió).
- **CP2077 no declara el intrínseco** si el prefix no existe (borrado o aún no creado) o si el archivo
  es un symlink o está bajo uno (aviso en el log): declararlo hacía fallar todo sync (symlink) o
  repoblaba un prefix borrado. Las entradas y la base quedan en el loadout hasta que el archivo vuelva.
- Los tests de la pieza 2 que ponían un *archivo* de mod en esa ruta o esperaban un External Change de
  archivo por una edición del juego pasan a entrada + base: la ruta es intrínseca.
- `LoadoutSettings` vive en `NexusMods.DataModel` (necesita `GetGame()` de `Abstractions.Games`).
- `GamePathAttribute` (Sdk) serializaba el id numérico de la ubicación y lo re-hasheaba al leer; sin
  usos hasta ahora. Escribe el nombre.

### Fuera de alcance

Formatos INI y lista de líneas (W3, Skyrim); UI para ver o editar entradas; orden por load order
entre entradas de distintos mods; productores de entradas desde instaladores o colecciones;
cualquier intrínseco de CP2077 que no sea `UserSettings.json`.
