using NexusMods.Abstractions.Loadouts;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.DataModel;

/// <summary>
/// Hashes that must stay in the file store: files of valid loadouts, every library file
/// (archive entries and loose top-level files) and game file backups.
/// </summary>
public static class LiveHashes
{
    public static HashSet<Hash> Collect(IDb db)
    {
        var live = new HashSet<Hash>();
        var loadoutValid = new Dictionary<LoadoutId, bool>();

        foreach (var file in LoadoutFile.All(db))
        {
            var item = new LoadoutItem.ReadOnly(db, file.Id);
            if (!loadoutValid.TryGetValue(item.LoadoutId, out var valid))
                loadoutValid[item.LoadoutId] = valid = item.Loadout.IsValid();
            if (valid) live.Add(file.Hash);
        }

        foreach (var file in LibraryFile.All(db))
            live.Add(file.Hash);

        foreach (var backup in GameBackedUpFile.All(db))
            live.Add(backup.Hash);

        return live;
    }
}
