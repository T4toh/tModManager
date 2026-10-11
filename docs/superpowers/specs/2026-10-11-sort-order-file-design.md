# Load order escrito a archivo (pieza 5)

Fecha: 2026-10-11 · Estado: diseño aprobado, pendiente de implementar en la rama `feat/sort-order-file`

## Objetivo

Que una variedad de sort order pueda declarar "este orden se escribe a este archivo con este formato"
y que el core lo escriba como archivo intrínseco en cada apply, detectando que falta aplicar cuando el
orden cambia. Es la pieza 5 de "Piezas genéricas para el segundo juego" en `TODO.md`. CP2077 la prueba
con el `modlist` de REDmod; Witcher 3 la usará para `Priority` de `mods.settings` y Skyrim/Fallout 4
para `plugins.txt`.

### Qué hay hoy

- `RedModDeployTool.Execute` genera el `modlist` en un archivo temporal (`WriteLoadOrderFile`), lo copia
  a `<prefix>/drive_c/modlist.txt` y corre `redMod.exe deploy -force -modlist=C:\modlist.txt` vía
  `deploy_redmod.bat` + protontricks. El loadout no es dueño del archivo: no se respalda, no se
  restaura, y su contenido no cuenta para "falta aplicar".
- `IIntrinsicFile` (pieza 4) escribe archivos del loadout en el apply (`ActionWriteIntrinsics` →
  `Ingest`, `AdaptLoadout` → `Ingest`), y `ShouldSynchronize` solo sabe si un intrínseco está al día
  cuando es `ISettingsIntrinsicFile` (`IsUpToDate`).
- `ALoadoutSynchronizer.IntrinsicFiles(loadout)` es virtual y devuelve vacío; `Cyberpunk2077Synchronizer`
  lo sobreescribe con `UserSettings.json` y sus guardas (ubicación declarada, carpeta del prefix existe,
  no es symlink ni está bajo uno, aviso una vez por path).
- `ISortOrderVariety` (`Abstractions.Games`) no sabe nada de archivos. La dependencia de proyectos es
  `Abstractions.Games → Abstractions.Loadouts.Synchronizers`, así que el synchronizer no puede ver las
  variedades.
- `Cyberpunk2077Synchronizer.Synchronize` corre `base.Synchronize` (pass 1, escribe intrínsecos), después
  el deploy de REDmod, después `base.Synchronize` de nuevo (pass 2).
- Los `SortOrderItem` están en `loadouts.TrackedEntitiesForLoadout`: reordenar en la UI refresca el
  estado del loadout y vuelve a evaluar `ShouldSynchronize`.

### Qué se decidió (2026-10-11)

- **Prueba con CP2077: `modlist.txt` del loadout en el prefix.** `drive_c/modlist.txt` entra a la
  whitelist de `LocationId.WinePrefix` y lo escribe el apply como intrínseco; `RedModDeployTool` deja de
  generarlo y solo corre el deploy apuntando a `C:\modlist.txt`. (Descartado: solo el motor genérico
  probado con el juego stub; escribirlo bajo `Game` y pasar una ruta `Z:\`.)
- **El enganche es una capacidad opcional de la variedad.** Una variedad implementa `ISortOrderFile`
  (ruta + render) y se registra también como `ISortOrderFile` en DI; `ALoadoutSynchronizer.IntrinsicFiles`
  por defecto junta las del juego del loadout. CP y W3 no tocan el synchronizer para esto. (Descartado:
  cada synchronizer compone a mano una clase base.)
- **El loadout es la fuente de verdad del archivo.** El juego no escribe `modlist.txt`; una edición a
  mano se pisa en el apply siguiente sin crear External Change. Archivos que el juego sí reescribe
  (`plugins.txt`) llegan con su juego y con un `Ingest` propio.
- **Sin orden por colección**: como hoy, `collectionId = None`.

## Diseño

### Core (`NexusMods.Abstractions.Loadouts.Synchronizers`)

```csharp
/// Un sort order que se escribe a un archivo del juego. Lo implementa la variedad.
public interface ISortOrderFile
{
    IEnumerable<GameId> GameIds { get; }
    /// Ruta del archivo (la ubicación puede no estar declarada: prefix inexistente).
    GamePath Path { get; }
    /// Texto completo del archivo para este loadout (el juego decide separador y fin de línea).
    string Render(Loadout.ReadOnly loadout, IDb db);
}
```

`SortOrderIntrinsicFile : IIntrinsicFile` (adaptador genérico, `sealed`):

- `Write` → bytes UTF-8 de `Render(loadout, loadout.Db)`.
- `Ingest(stream)` → lee el disco; si es igual a `Render` devuelve `null`, si difiere devuelve `Render`
  (el loadout gana). No crea entradas ni toca la transacción.
- `IsUpToDate(diskText)` → `diskText == Render`.

`IIntrinsicFile` gana `bool IsUpToDate(string diskText, Loadout.ReadOnly loadout) => false;` como
método default. `ISettingsIntrinsicFile` ya lo declara; `ShouldSynchronize` llama `file.IsUpToDate` sin
el `is ISettingsIntrinsicFile`.

`ALoadoutSynchronizer`:

- `IntrinsicFiles(loadout)` deja de devolver vacío: por cada `ISortOrderFile` del `IServiceProvider` cuyo
  `GameIds` contenga el juego del loadout, `DeclareIntrinsic(loadout, file.Path, path => new SortOrderIntrinsicFile(file))`.
- `DeclareIntrinsic(loadout, path, factory)` (protected, nuevo) concentra las guardas que hoy viven en CP:
  ubicación declarada (`Locations.TryGetValue`), carpeta raíz existe, `IsManaged(path)` (si la ubicación
  tiene whitelist), no es symlink ni está bajo uno (aviso una vez por path, `HashSet` en la instancia).
  Devuelve `null` cuando no se declara.
- `Cyberpunk2077Synchronizer.IntrinsicFiles` pasa a `base.IntrinsicFiles(loadout)` + `DeclareIntrinsic`
  de `UserSettings.json`. Se van sus guardas y su `HashSet`.

### REDmod (`NexusMods.Games.RedEngine`)

- `RedModSortOrderVariety : ISortOrderFile`: `GameIds = [Cyberpunk2077Game.GameId]`,
  `Path = new GamePath(LocationId.WinePrefix, "drive_c/modlist.txt")`,
  `Render` = `GetRedModOrder(loadout.LoadoutId, None, db)` unido con `"\r\n"` (REDmod solo acepta CRLF),
  vacío sin REDmods. Registro: `AddSingleton<ISortOrderFile>(p => p.GetRequiredService<RedModSortOrderVariety>())`.
- `Cyberpunk2077Game.GetManagedFiles` suma `drive_c/modlist.txt` a la whitelist del prefix. La ruta es
  una constante compartida con la variedad: `Constants.RedModListPath` en `Constants.cs`, junto a `RedModPath`.
- `RedModDeployTool.Execute`: ya no escribe ni copia nada. Si `Locations.ToAbsolutePath(ModListPath)` no
  existe (prefix sin crear, archivo no declarado) loguea y sale sin correr el deploy. Corre
  `deploy -force -modlist=C:\modlist.txt` como hoy. Se borra `WriteLoadOrderFile`, la rama `IsWindows`
  (muerta en este fork) y la dependencia de `TemporaryFileManager` para el modlist (queda para el `.bat`).
- Orden de ejecución: `Synchronize` pass 1 escribe el intrínseco antes de llamar al tool, así el deploy
  siempre lee el orden recién aplicado.

### Comportamiento con la whitelist (pieza 2, sin cambios)

- `modlist.txt` preexistente (otro manager) se adopta como original y se respalda; dejar de gestionar lo
  restaura. Creado por nosotros, dejar de gestionar lo borra y nunca borra carpetas del prefix.
- Symlink en `drive_c/modlist.txt` o bajo uno: no se declara el intrínseco (aviso una vez), el apply
  sigue, el deploy no corre (archivo "no existe" para nosotros). Mismo criterio que `UserSettings.json`.

### Qué no entra

- Formato INI / `Priority` de W3, `plugins.txt` y su `Ingest` con External Change.
- Orden por colección en el archivo.
- UI nueva: la página de load order ya existe.

## Criterios de éxito

1. Con REDmods en el loadout y prefix existente, el apply escribe `drive_c/modlist.txt` con los REDmods
   habilitados en orden, CRLF, sin línea final extra respecto a hoy (los `.verified.` de
   `RedModDeployToolTests` se conservan).
2. Reordenar en la UI deja el loadout en "falta aplicar"; aplicar reescribe el archivo.
3. Editar el archivo a mano y aplicar lo restaura; no aparece ningún External Change.
4. Deshabilitar un mod con REDmod y aplicar lo quita del archivo.
5. Sin prefix (no lanzado nunca, o borrado desde el Storage Manager): no hay archivo, el apply no falla,
   el deploy no corre.
6. `drive_c/modlist.txt` symlink a un archivo de afuera: el apply no escribe a través, el archivo de
   afuera queda intacto.
7. Archivo preexistente antes de gestionar: se respalda, y dejar de gestionar lo devuelve byte a byte.
8. Prueba real con el juego: cambiar el orden en la UI, aplicar, confirmar el contenido de
   `drive_c/modlist.txt` y que el deploy de REDmod lo use (log de redMod, `r6/cache/modded` regenerado).

## Tests

- `NexusMods.DataModel.Synchronizer.Tests/SortOrderFileTests.cs` (fixture `ACyberpunkIsolatedGameTest`
  con `WithWinePrefix`): criterios 1 a 7. Los REDmods se agregan como en `RedModDeployToolTests.AddRedMods`
  (los `.7z` de `LibraryArchiveInstallerTests/Resources`, ya referenciados por el proyecto de tests de CP; se
  copian al de Synchronizer o el helper se mueve a `Games.TestFramework`).
- `RedModDeployToolTests`: `WriteLoadoutFile` pasa a `variety.Render(loadout, loadout.Db)`; sin cambios en
  los `.verified.`.
- Test negativo obligatorio para el symlink (criterio 6): tiene que fallar si se saca la guarda.

## Riesgos

- `Render` lee el sort order de `loadout.Db` (la revisión que aplica), igual que `WriteLoadOrderFile`
  hoy: si el apply corre con un loadout viejo, escribe el orden viejo. No cambia respecto a hoy.
- `GetServices<ISortOrderFile>()` en el constructor del synchronizer puede crear ciclos (la variedad toma
  `IServiceProvider`, no el synchronizer): resolver de forma perezosa (`Lazy`) dentro de `IntrinsicFiles`.
