using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using System.Collections.Frozen;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.MnemonicDB.Abstractions;

namespace NexusMods.Games.RedEngine.Cyberpunk2077.Emitters;

public class CoreModsDiagnosticEmitter : ILoadoutDiagnosticEmitter
{
    private static readonly string[] RedundantFolderNames = ["Cyberpunk 2077", "Cyberpunk2077"];
    private static readonly string[] CoreGameFolders = ["bin", "r6", "red4ext", "archive", "engine", "mods"];

    public async IAsyncEnumerable<Diagnostic> Diagnose(
        Loadout.ReadOnly loadout,
        FrozenDictionary<GamePath, SyncNode> syncTree,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 1. Check for redundant folder structures in the loadout
        var loadoutItems = LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath();
        var checkedGroups = new HashSet<EntityId>();
        var undeployed = new Dictionary<EntityId, (string ModName, List<string> Paths)>();

        foreach (var item in loadoutItems)
        {
            var targetPath = (GamePath)item.TargetPath;
            if (targetPath.LocationId != LocationId.Game) continue;

            // The synchronizer never touches the game's own archive folders: say so instead of failing silently
            var file = item.AsLoadoutItem();
            if (Cyberpunk2077Synchronizer.IsVanillaContentPath(targetPath) && file.HasParent() && file.IsEnabled())
            {
                if (!undeployed.TryGetValue(file.Parent.Id, out var entry))
                    undeployed[file.Parent.Id] = entry = (file.Parent.AsLoadoutItem().Name, []);
                entry.Paths.Add(targetPath.Path.ToString());
            }

            var pathString = targetPath.Path.ToString();
            var parts = pathString.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length > 1)
            {
                // Case: Cyberpunk 2077/bin/...
                if (RedundantFolderNames.Any(name => string.Equals(parts[0], name, StringComparison.OrdinalIgnoreCase)) &&
                    CoreGameFolders.Any(core => string.Equals(parts[1], core, StringComparison.OrdinalIgnoreCase)))
                {
                    var loadoutItem = item.AsLoadoutItem();
                    if (loadoutItem.HasParent())
                    {
                        var group = loadoutItem.Parent;
                        if (checkedGroups.Add(group.Id))
                        {
                            yield return Diagnostics.CreateRedundantFolderDetected(
                                group.AsLoadoutItem().Name,
                                string.Join('/', parts.Take(2))
                            );
                        }
                    }
                }
            }
        }

        foreach (var (modName, paths) in undeployed.Values)
            yield return Diagnostics.CreateUndeployedVanillaContentFiles(modName, paths.Count, string.Join("`, `", paths.Order(StringComparer.Ordinal).Take(3)));

        // 2. Redscript compilation check
        // Check if there are any .reds files in the loadout that go to r6/scripts
        bool hasScripts = syncTree.Keys.Any(p => p.LocationId == LocationId.Game && p.Path.ToString().StartsWith("r6/scripts", StringComparison.OrdinalIgnoreCase) && p.Path.ToString().EndsWith(".reds", StringComparison.OrdinalIgnoreCase));
        
        if (hasScripts)
        {
            bool hasCache;
            if (syncTree.Count > 0)
            {
                hasCache = syncTree.Keys.Any(p => p.LocationId == LocationId.Game && p.Path.ToString().StartsWith("r6/cache", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                // Background diagnostic fallback: check database for scanned disk state
                hasCache = DiskStateEntry.FindByGame(loadout.Db, loadout.Installation)
                    .Any(e => e.Path.Item3.ToString().StartsWith("r6/cache", StringComparison.OrdinalIgnoreCase));
            }

            if (!hasCache)
            {
                yield return Diagnostics.CreateRedscriptCompilationFailed("r6/logs/redscript.log");
            }
        }

        await Task.Yield();
    }

    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);
}
