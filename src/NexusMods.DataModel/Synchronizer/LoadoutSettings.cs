using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.DataModel.Synchronizer;

/// <summary>Entries of intrinsic settings files set by hand (CLI), kept in the loadout's "Ajustes" group.</summary>
public static class LoadoutSettings
{
    public const string GroupName = "Ajustes";

    /// <summary>
    /// Resolves which intrinsic file an entry targets: <paramref name="fileArg"/> as "Location:relative/path",
    /// or the game's only intrinsic file when omitted.
    /// </summary>
    public static bool TryResolveFile(Loadout.ReadOnly loadout, string? fileArg, out GamePath path, out IIntrinsicFile file, out string? error)
    {
        path = default;
        file = null!;
        error = null;
        var intrinsics = ((ALoadoutSynchronizer)loadout.InstallationInstance.GetGame().Synchronizer).IntrinsicFiles(loadout);
        if (string.IsNullOrWhiteSpace(fileArg))
        {
            if (intrinsics.Count != 1)
            {
                error = intrinsics.Count == 0
                    ? "Este juego no declara archivos de configuración gestionados (¿falta el prefix de Wine?)"
                    : "Hay varios archivos de configuración; indicá cuál con -f Ubicación:ruta. Opciones: " + string.Join(", ", intrinsics.Keys);
                return false;
            }
            (path, file) = (intrinsics.Keys.First(), intrinsics.Values.First());
            return true;
        }
        var colon = fileArg.IndexOf(':');
        if (colon <= 0)
        {
            error = $"'{fileArg}' no tiene la forma Ubicación:ruta (ej. WinePrefix:drive_c/...)";
            return false;
        }
        path = new GamePath(LocationId.From(fileArg[..colon]), (RelativePath)fileArg[(colon + 1)..]);
        if (!intrinsics.TryGetValue(path, out file!))
        {
            error = $"'{fileArg}' no es un archivo de configuración gestionado. Opciones: " + string.Join(", ", intrinsics.Keys);
            return false;
        }
        return true;
    }

    public static async Task<IntrinsicFileEntry.ReadOnly> Upsert(IConnection connection, Loadout.ReadOnly loadout, GamePath file, string key, string value)
    {
        var db = connection.Db;
        using var tx = connection.BeginTransaction();
        var group = LoadoutItem.FindByLoadout(db, loadout.LoadoutId)
            .Where(i => i.Name == GroupName && !i.HasParent())
            .Select(i => (EntityId?)i.Id)
            .FirstOrDefault();
        if (group is null)
        {
            var created = new LoadoutItemGroup.New(tx, out var newGroupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, newGroupId) { Name = GroupName, LoadoutId = loadout.LoadoutId },
            };
            group = created.Id;
        }
        var groupId = group.Value;

        var existing = IntrinsicFileEntry.FindByFile(db, file)
            .FirstOrDefault(e => e.Key == key && e.AsLoadoutItem().LoadoutId == loadout.LoadoutId
                                 && LoadoutItem.Parent.TryGetValue(e.AsLoadoutItem(), out var p) && p.Value == groupId);
        if (existing.IsValid())
        {
            if (existing.Value != value) tx.Add(existing.Id, IntrinsicFileEntry.Value, value);
            await tx.Commit();
            return IntrinsicFileEntry.Load(connection.Db, existing.Id);
        }

        var entry = new IntrinsicFileEntry.New(tx, out var id)
        {
            File = file, Key = key, Value = value,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = key, LoadoutId = loadout.LoadoutId, ParentId = LoadoutItemGroupId.From(groupId) },
        };
        var result = await tx.Commit();
        return result.Remap(entry);
    }

    /// <summary>
    /// Deletes the entry for <paramref name="key"/>: the hand-set one in the "Ajustes" group, or with
    /// <paramref name="external"/> the External Change the game produced. False when there was none.
    /// </summary>
    public static async Task<bool> Remove(IConnection connection, Loadout.ReadOnly loadout, GamePath file, string key, bool external)
    {
        var db = connection.Db;
        var entry = IntrinsicFileEntry.FindByFile(db, file)
            .FirstOrDefault(e => e.Key == key && e.AsLoadoutItem().LoadoutId == loadout.LoadoutId
                                 && LoadoutItem.Parent.TryGetValue(e.AsLoadoutItem(), out var p)
                                 && (external ? LoadoutOverridesGroup.Load(db, p).IsValid() : LoadoutItem.Load(db, p).Name == GroupName));
        if (!entry.IsValid()) return false;
        using var tx = connection.BeginTransaction();
        tx.Delete(entry.Id, recursive: false);
        await tx.Commit();
        return true;
    }

    public static IEnumerable<(GamePath File, string Key, string Value, string Group, bool Wins)> List(Loadout.ReadOnly loadout)
    {
        var db = loadout.Db;
        var intrinsics = ((ALoadoutSynchronizer)loadout.InstallationInstance.GetGame().Synchronizer).IntrinsicFiles(loadout);
        foreach (var (path, file) in intrinsics)
        {
            var winners = file is ISettingsIntrinsicFile settings
                ? settings.WinningEntries(loadout).ToDictionary(kv => kv.Key, kv => kv.Value.Id)
                : new Dictionary<string, EntityId>();
            foreach (var entry in IntrinsicFileEntry.FindByFile(db, path).Where(e => e.AsLoadoutItem().LoadoutId == loadout.LoadoutId))
            {
                var item = entry.AsLoadoutItem();
                var group = LoadoutItem.Parent.TryGetValue(item, out var parent) ? LoadoutItem.Load(db, parent).Name : "(sin grupo)";
                yield return (path, entry.Key, entry.Value, group, winners.TryGetValue(entry.Key, out var w) && w == entry.Id);
            }
        }
    }
}
