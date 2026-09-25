using Microsoft.Extensions.DependencyInjection;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.App.UI.Helpers;

/// <summary>Shared lookup for the Steam library that holds the managed Cyberpunk 2077 install.</summary>
public static class SteamPaths
{
    /// <summary>The Steam library that holds the first managed game (the folder containing <c>steamapps</c>), or null.</summary>
    public static AbsolutePath? LibraryRoot(IServiceProvider serviceProvider)
    {
        var db = serviceProvider.GetRequiredService<IConnection>().Db;
        foreach (var loadout in Loadout.All(db))
        {
            if (!loadout.IsVisible()) continue;
            return FindLibraryRoot(loadout.InstallationInstance.Locations[LocationId.Game].Path);
        }

        return null;
    }

    /// <summary>Walks up from <paramref name="gamePath"/> (<c>&lt;lib&gt;/steamapps/common/&lt;game&gt;</c>) to the directory containing <c>steamapps</c>.</summary>
    internal static AbsolutePath? FindLibraryRoot(AbsolutePath gamePath)
    {
        for (var dir = gamePath; ; dir = dir.Parent)
        {
            if (dir.Combine("steamapps").DirectoryExists()) return dir;
            if (dir == dir.Parent) return null;
        }
    }
}
