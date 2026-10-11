using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileSystemGlobbing;
using NexusMods.Abstractions.Cli;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.DataModel.Synchronizer;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.DataModel.Undo;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.DataModel.CommandLine.Verbs;

/// <summary>
/// Loadout management verbs for the commandline interface
/// </summary>
public static class LoadoutManagementVerbs
{
    /// <summary>
    /// Register the loadout management verbs
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection AddLoadoutManagementVerbs(this IServiceCollection services) =>
        services
            .AddModule("loadouts", "Commands for managing loadouts as a whole")
            .AddModule("loadout", "Commands for managing a specific loadout")
            .AddModule("loadout groups", "Commands for managing the file groups in a loadout")
            .AddModule("loadout group", "Commands for managing a specific group of files in a loadout")
            .AddModule("loadout group items", "Commands for managing the items in a group of files in a loadout")
            .AddModule("loadout version", "Commands for managing the version of a loadout")
            .AddModule("loadout settings", "Entradas de archivos de configuración que el loadout posee (UserSettings.json en CP2077)")
            .AddVerb(() => SetVersion)
            .AddVerb(() => Synchronize)
            .AddVerb(() => InstallMod)
            .AddVerb(() => Reindex)
            .AddVerb(() => ListLoadouts)
            .AddVerb(() => BackupFiles)
            .AddVerb(() => ListGroupContents)
            .AddVerb(() => ListGroups)
            .AddVerb(() => DeleteGroupItems)
            .AddVerb(() => ListRevisions)
            .AddVerb(() => Revert)
            .AddVerb(() => SettingsSet)
            .AddVerb(() => SettingsUnset)
            .AddVerb(() => SettingsList);

    [Verb("loadout settings set", "Fija el valor de una clave de un archivo de configuración gestionado")]
    private static async Task<int> SettingsSet([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout")] Loadout.ReadOnly loadout,
        [Option("k", "key", "Clave (CP2077: grupo/opción, ej. /graphics/advanced/DLSS)")] string key,
        [Option("v", "value", "Valor como literal del formato (CP2077: JSON, ej. \"Off\", 5.0, true)")] string value,
        [Option("f", "file", "Archivo como Ubicación:ruta; por defecto el único del juego", isOptional: true)] string? file,
        [Injected] IConnection connection)
    {
        if (!LoadoutSettings.TryResolveFile(loadout, file, out var path, out var intrinsic, out var error))
        {
            await renderer.Error("{0}", error!);
            return -1;
        }
        if (intrinsic is ISettingsIntrinsicFile settings && !settings.TryValidate(key, value, out var invalid))
        {
            await renderer.Error("{0}", invalid!);
            return -1;
        }
        var entry = await LoadoutSettings.Upsert(connection, loadout, path, key, value);
        await renderer.TextLine("{0} = {1} en {2} (grupo '{3}'). Aplicá el loadout para escribirlo.", entry.Key, entry.Value, path, LoadoutSettings.GroupName);
        return 0;
    }

    [Verb("loadout settings unset", "Quita una clave fijada con 'loadout settings set', o con -g Overrides el External Change que dejó el juego")]
    private static async Task<int> SettingsUnset([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout")] Loadout.ReadOnly loadout,
        [Option("k", "key", "Clave")] string key,
        [Option("f", "file", "Archivo como Ubicación:ruta; por defecto el único del juego", isOptional: true)] string? file,
        [Option("g", "group", "Ajustes (por defecto) u Overrides", isOptional: true)] string? group,
        [Injected] IConnection connection)
    {
        if (!LoadoutSettings.TryResolveFile(loadout, file, out var path, out _, out var error))
        {
            await renderer.Error("{0}", error!);
            return -1;
        }
        var external = string.Equals(group, "Overrides", StringComparison.OrdinalIgnoreCase);
        if (!external && group is not null && !string.Equals(group, LoadoutSettings.GroupName, StringComparison.OrdinalIgnoreCase))
        {
            await renderer.Error("-g tiene que ser '{0}' u 'Overrides'", LoadoutSettings.GroupName);
            return -1;
        }
        if (!await LoadoutSettings.Remove(connection, loadout, path, key, external))
        {
            await renderer.Error("No hay ninguna entrada '{0}' en el grupo '{1}'", key, external ? "Overrides" : LoadoutSettings.GroupName);
            return -1;
        }
        await renderer.TextLine("{0} quitada. Aplicá el loadout; la clave conserva el último valor escrito hasta que el juego o vos la cambien.", key);
        return 0;
    }

    [Verb("loadout settings list", "Lista las entradas de archivos de configuración del loadout")]
    private static async Task<int> SettingsList([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout")] Loadout.ReadOnly loadout)
    {
        await LoadoutSettings.List(loadout)
            .Select(r => (r.File.ToString(), r.Key, r.Value, r.Group, r.Wins ? "sí" : ""))
            .RenderTable(renderer, "Archivo", "Clave", "Valor", "Grupo", "Gana");
        return 0;
    }

    [Verb("loadout version set", "Sets the game version for a loadout")]
    private static async Task<int> SetVersion([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to set the version for")] Loadout.ReadOnly loadout,
        [Option("v", "version", "Version to set")] string version,
        [Injected] IFileHashesService hasherService)
    {
        if (!hasherService.TryGetLocatorIdsForVanityVersion(loadout.InstallationInstance, VanityVersion.From(version), out var newCommonIds))
        {
            await renderer.Error("Version {0} not found", version);
            return -1;
        }

        // Get the actual version from the ids, so that we can sanitize the version string, and collapse multiple
        // versions into a single version string
        if (!hasherService.TryGetVanityVersion((loadout.Installation.Store, newCommonIds), out var actualVersion))
        {
            await renderer.Error("Version {0} not found", version);
            return -1;
        }
        
        using var tx = loadout.Db.Connection.BeginTransaction();

        loadout = loadout.Rebase();
        // Retract the old ids
        foreach (var existingId in loadout.LocatorIds)
            tx.Retract(loadout, Loadout.LocatorIds, existingId);
        // And the old version
        tx.Retract(loadout, Loadout.GameVersion, loadout.GameVersion);
        
        // Add the new ids and version
        foreach (var newId in newCommonIds)
            tx.Add(loadout, Loadout.LocatorIds, newId);
        
        tx.Add(loadout, Loadout.GameVersion, actualVersion);
        
        await tx.Commit();
        
        return 0;
    }

    [Verb("loadout synchronize", "Synchronize the loadout with the game folders, adding any changes in the game folder to the loadout and applying any new changes in the loadout to the game folder")]
    private static async Task<int> Synchronize([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to apply")] Loadout.ReadOnly loadout,
        [Injected] ISynchronizerService syncService)
    {
        await syncService.Synchronize(loadout);
        return 0;
    }

    [Verb("loadout backup-game-files", "Archives all the files on disk that are otherwise not backed up")]
    private static async Task<int> BackupFiles(
        [Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to backup files for")]
        Loadout.ReadOnly loadout,
        [Injected] IFileStore fileStore)
    {
        var game = loadout.InstallationInstance.GetGame();
        var gameInstallation = loadout.InstallationInstance;
        var tree = await game.Synchronizer.BuildSyncTree(loadout);
        game.Synchronizer.ProcessSyncTree(tree);
        var toBackup = tree.Where(f => f.Value.HaveDisk && 
                                       !f.Value.Signature.HasFlag(Signature.DiskArchived) && 
                                       f.Value.SourceItemType == LoadoutSourceItemType.Game)
            .Select(f => (Path: gameInstallation.Locations.ToAbsolutePath(f.Key), f.Value.Disk.Size, f.Value.Disk.Hash))
            .ToArray();
        await renderer.TextLine("Backing up {0} files for a total of {1}", toBackup.Length, toBackup.Aggregate(Size.Zero, (a, b) => a + b.Size));
        
        var entries = toBackup.Select(f => new ArchivedFileEntry(new NativeFileStreamFactory(f.Path), f.Hash, f.Size)).ToArray();
        await fileStore.BackupFiles(entries);
        return 0;
    }

    
    [Verb("loadout install", "Installs a mod into a loadout")]
    private static async Task<int> InstallMod([Injected] IRenderer renderer,
        [Option("l", "loadout", "loadout to add the mod to")] Loadout.ReadOnly loadout,
        [Option("f", "file", "Mod file to install")] AbsolutePath file,
        [Option("n", "name", "Name of the mod after installing")] string name,
        [Option("v", "version", "Version to record for the file", isOptional: true)] string? version,
        [Option("s", "source", "Where the file came from (mod.io, GitHub, foro...)", isOptional: true)] string? source,
        [Option("u", "url", "Page of the mod", isOptional: true)] Uri? url,
        [Injected] ILibraryService libraryService,
        [Injected] ILoadoutManager loadoutManager,
        [Injected] CancellationToken token)
    {
        return await renderer.WithProgress(token, async () =>
        {
            var localFile = await libraryService.AddLocalFile(file, new LocalFileMetadata(Name: name, Version: version, Source: source, PageUri: url));
            await loadoutManager.InstallItem(localFile.AsLibraryFile().AsLibraryItem(), loadout);
            return 0;
        });
    }
    
    [Verb("loadout reindex", "Re-indexes the on-disk state of the loadout")]
    private static async Task<int> Reindex([Injected] IRenderer renderer,
        [Option("l", "loadout", "loadout to add the mod to")] Loadout.ReadOnly loadout,
        [Injected] CancellationToken token)
    {
        await renderer.Text("Reindexing {0}", loadout.Name);
        var synchronizer = loadout.InstallationInstance.GetGame().Synchronizer;
        await synchronizer.ReindexState(loadout.InstallationInstance);
        return 0;
    }

    [Verb("loadouts list", "Lists all the loadouts")]
    private static async Task<int> ListLoadouts([Injected] IRenderer renderer,
        [Injected] IConnection conn,
        [Injected] CancellationToken token)
    {
        var db = conn.Db;
        await Loadout.All(db)
            .Where(x => x.IsVisible())
            .Select(list => (list.LoadoutId, list.Name, list.Installation.Name, list.GameVersion, list.Installation.Store, LoadoutItem.FindByLoadout(db, list.Id)))
            .RenderTable(renderer, "Id", "Name", "Game", "Version", "Store", "Items");
        return 0;
    }

    [Verb("loadout group list", "Lists the contents of a loadout group")]
    private static async Task<int> ListGroupContents([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to load")] Loadout.ReadOnly loadout,
        [Option("g", "group", "Name of the group to list")] string groupName,
        [Option("f", "filterFiles", "Filter files by the given glob", true)] Matcher? filterFiles,
        [Injected] CancellationToken token)
    {
        var mod = LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemGroup()
            .First(m => m.AsLoadoutItem().Name == groupName);

        if (!mod.IsValid())
            return await renderer.InputError("Group {0} not found", groupName);
        
        Func<string, bool> filter = _ => true;

        if (filterFiles != null) 
            filter = s => filterFiles.Match(s).HasMatches;
        
        await mod.Children
            .Select(c =>
                {
                    var hasPath = c.TryGetAsLoadoutItemWithTargetPath(out var withPath);
                    var hasFile = withPath.TryGetAsLoadoutFile(out var withFile);

                    if (hasPath && hasFile)
                        return (withPath.TargetPath.Item2, withPath.TargetPath.Item3, withFile.Hash.ToString());

                    if (hasPath)
                        return (withPath.TargetPath.Item2, withPath.TargetPath.Item3, "<none>");

                    return default((LocationId, RelativePath, string));

                })
            .Where(v => v != default((LocationId, RelativePath, string)))
            .Where(f => filter(f.Item2.ToString()))
            .OrderBy(v => v.Item1)
            .ThenBy(v => v.Item2)
            .RenderTable(renderer, "Folder", "File", "Hash");
        return 0;
    }

    [Verb("loadout group items delete", "Deletes items from a group that match a given pattern")]
    private static async Task<int> DeleteGroupItems(
        [Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to load")] Loadout.ReadOnly loadout,
        [Option("g", "group", "Name of the group to list")] string groupName,
        [Option("f", "filterFiles", "Filter files by the given glob")] Matcher filterFiles,
        [Injected] CancellationToken token)
    {
        var mod = LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemGroup()
            .First(m => m.AsLoadoutItem().Name == groupName);
        
        if (!mod.IsValid())
            return await renderer.InputError("Group {0} not found", groupName);

        var ids = mod.Children
            .OfTypeLoadoutItemWithTargetPath()
            .Where(t => filterFiles.Match(t.TargetPath.Item3.ToString()).HasMatches)
            .Select(f => f.Id)
            .ToArray();
        
        await renderer.Text("Deleting {0} items", ids.Length);
        
        using var tx = loadout.Db.Connection.BeginTransaction();
        foreach (var id in ids)
            tx.Delete(id, false);
        await tx.Commit();
        
        await renderer.Text("Complete", ids.Length);
        
        return 0;
    }

    [Verb("loadout groups list", "Lists the groups in a loadout")]
    private static async Task<int> ListGroups([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to load")] Loadout.ReadOnly loadout,
        [Injected] CancellationToken token)
    {
        await LoadoutItem.FindByLoadout(loadout.Db, loadout)
            .OfTypeLoadoutItemGroup()
            .Select(mod => (mod.AsLoadoutItem().Name, mod.Children.Count))
            .RenderTable(renderer, "Name", "Items");

        return 0;
    }

    [Verb("loadout revisions", "Lists revisions for a loadout")]
    private static async Task<int> ListRevisions([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to load")] Loadout.ReadOnly loadout,
        [Injected] IConnection connection,
        [Injected] UndoService undoService,
        [Injected] CancellationToken token)
    {
        var revisions = undoService.RevisionsFor(loadout);

        await revisions.OrderBy(r => r.Revision.Timestamp)
            .Select((r, idx) => (idx, r.Revision.Timestamp, r.Revision.TxEntity, r.ModCount))
            .RenderTable(renderer, "Rev #", "Timestamp", "Tx", "ModCount");

        return 0;
    }

    [Verb("loadout revert", "Reverts a loadout to a specific revision")]
    private static async Task<int> Revert([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to load")] Loadout.ReadOnly loadout,
        [Option("r", "revisionNumber", "Revision number to revert to")] int revisionNumber,
        [Injected] IConnection connection,
        [Injected] UndoService undoService,
        [Injected] CancellationToken token)
    {
        
        var revisions = undoService.RevisionsFor(loadout);
        
        var revision = revisions.OrderBy(r => r.Revision.Timestamp)
            .Select((r, idx) => (Idx: idx, r.Revision))
            .FirstOrDefault(row => row.Idx == revisionNumber);
        
        await undoService.RevertTo(revision.Revision);
        
        return 0;
    }
}
