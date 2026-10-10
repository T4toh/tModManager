using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Sdk.Settings;
using NexusMods.Games.RedEngine.Cyberpunk2077.Emitters;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;
using R3;

namespace NexusMods.Games.RedEngine.Cyberpunk2077;

public class Cyberpunk2077Synchronizer : ALoadoutSynchronizer
{
    private Cyberpunk2077Settings _settings;
    
    /// <summary>
    /// Redmod deploys combined mods to the redmod cache folder
    /// </summary>
    private static GamePath RedModCacheFolder => new(LocationId.Game, "r6/cache/modded");
    
    /// <summary>
    /// Redmod stages the scripts in the redmod/scripts folder
    /// </summary>
    private static GamePath RedModScriptsFolder => new(LocationId.Game, "tools/redmod/scripts");
    
    /// <summary>
    /// Redmod stages the tweaks in the redmod/tweaks folder
    /// </summary>
    private static GamePath RedModTweaksFolder => new(LocationId.Game, "tools/redmod/tweaks");
    
    private static GamePath ArchivePcContentFolder => new(LocationId.Game, "archive/pc/content");
    
    private static GamePath ArchivePcEp1Folder => new(LocationId.Game, "archive/pc/ep1");
    
    
    private readonly RedModDeployTool _redModTool;
    
    private static bool IsVanillaContentPath(GamePath path)
    {
        if (path.LocationId != LocationId.Game) return false;
        var pathStr = path.Path.ToString().Replace('\\', '/').ToLowerInvariant();
        
        // Cyberpunk 2077 specific: archive/pc/content and archive/pc/ep1 contain 90GB of data.
        // archive/pc/mod is where mods go, so we must NOT ignore that.
        // We ignore everything else in archive/pc/ to be safe.
        if (pathStr.StartsWith("archive/pc/") && !pathStr.StartsWith("archive/pc/mod"))
            return true;

        // Also ignore the deep clean backup folders just in case
        if (pathStr.Contains("_mod_remover_backup_"))
            return true;

        // r6/logs/ contains runtime logs from Redscript, RED4ext, etc.
        // These are generated every time the game runs and are not mod files.
        if (pathStr.StartsWith("r6/logs/"))
            return true;

        return false;
    }

    /// <summary>UserSettings.json is generated from the loadout's entries when the prefix location exists.</summary>
    public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout)
    {
        var locations = loadout.InstallationInstance.Locations;
        if (!locations.TryGetValue(LocationId.WinePrefix, out var prefix)) return new Dictionary<GamePath, IIntrinsicFile>();
        // Prefix deleted (Storage Manager) or not created yet: nothing to generate inside a folder that is not there.
        // The loadout keeps its entries and base; the file comes back once the prefix exists again.
        if (!prefix.Path.DirectoryExists()) return new Dictionary<GamePath, IIntrinsicFile>();
        var path = new GamePath(LocationId.WinePrefix, Cyberpunk2077Game.UserSettingsPath(prefix.Path));
        var resolved = locations.ToAbsolutePath(path);
        // A linked settings file (or one behind a linked folder) is never written through, and declaring it
        // would make every sync refuse: leave it alone and say so, the rest of the loadout still applies.
        if (SafePath.IsSymlink(resolved) || SafePath.IsUnderSymlink(prefix.Path.ToString(), resolved.ToString()))
        {
            Logger.LogWarning("`{Path}` es un symlink o está bajo uno; tModManager no gestiona sus ajustes", path);
            return new Dictionary<GamePath, IIntrinsicFile>();
        }
        return new Dictionary<GamePath, IIntrinsicFile> { [path] = new UserSettingsFile(path) };
    }

    // Return true to filter OUT (skip), false to include.
    // We filter out vanilla content (90GB archives) and deep-clean backup dirs.
    protected override IGamePathFilter GamePathFilter => GamePathFilters.Create(IsVanillaContentPath);
    
    public override bool IsIgnoredBackupPath(GamePath path)
    {
        if (_settings.DoFullGameBackup)
            return false;
        
        if (path.LocationId != LocationId.Game)
            return false;

        if (IsVanillaContentPath(path))
            return true;

        var pathStr = path.Path.ToString().Replace('\\', '/').ToLowerInvariant();
        if (pathStr.StartsWith("_mod_remover_backup_") || pathStr.Contains("/_mod_remover_backup_"))
            return true;
        
        return IgnoredBackupFolders.Any(ignore => path.Path.InFolder(ignore.Path));
    }

    public override void ProcessSyncTree(Dictionary<GamePath, SyncNode> syncTree)
    {
        base.ProcessSyncTree(syncTree);
        
        // Final safety pass: filtered paths (vanilla content, backup dirs) must never be touched.
        // Force DoNothing so ExtractToDisk / BackupFile / DeleteFromDisk don't fire on them.
        foreach (var path in syncTree.Keys.ToArray())
        {
            if (IsVanillaContentPath(path))
                syncTree[path] = syncTree[path] with { Actions = Actions.DoNothing };
        }

        // Debug summary
        var groups = syncTree.GroupBy(x => x.Value.Actions).OrderByDescending(g => g.Count());
        Logger.LogDebug("[SYNC] Tree summary ({Total} files):", syncTree.Count);
        foreach (var g in groups)
            Logger.LogDebug("[SYNC]   {Action} => {Count} files", g.Key, g.Count());
    }

    public override async Task ActionBackupNewFiles(GameInstallation installation, GameInstallMetadataId installMetadataId, Dictionary<GamePath, SyncNode> files)
    {
        var filteredFiles = files
            .Where(x => !IsVanillaContentPath(x.Key))
            .ToDictionary(x => x.Key, x => x.Value);
        
        await base.ActionBackupNewFiles(installation, installMetadataId, filteredFiles);
    }

    protected internal Cyberpunk2077Synchronizer(IServiceProvider provider) : base(provider)
    {
        var settingsManager = provider.GetRequiredService<ISettingsManager>();

        _settings = settingsManager.Get<Cyberpunk2077Settings>();
        settingsManager.GetChanges<Cyberpunk2077Settings>(prependCurrent: false).Subscribe(value => _settings = value);
        _redModTool = provider.GetServices<ITool>().OfType<RedModDeployTool>().First();
    }

    private static readonly GamePath[] IgnoredBackupFolders =
    [
        ArchivePcContentFolder,
        ArchivePcEp1Folder,
    ];

    public override async Task<Loadout.ReadOnly> Synchronize(Loadout.ReadOnly loadout, SynchronizeLoadoutJob? job)
    {
        Logger.LogDebug("[SYNC] Cyberpunk2077Synchronizer.Synchronize START (pass 1)");
        loadout = await base.Synchronize(loadout, job);
        Logger.LogDebug("[SYNC] Cyberpunk2077Synchronizer.Synchronize END (pass 1)");
        if (!MissingRedModEmitter.HasRedMods(loadout, out _, out var numRedModDirs)) return loadout;
        if (!MissingRedModEmitter.HasRedModToolInstalled(loadout, out _))
        {
            Logger.LogWarning("RedMod tool isn't installed but the loadout contains `{Count}` red mods", numRedModDirs);
            return loadout;
        }

        await _redModTool.Execute(loadout, CancellationToken.None);
        Logger.LogDebug("[SYNC] Cyberpunk2077Synchronizer.Synchronize START (pass 2, after RedMod)");
        loadout = await base.Synchronize(loadout, job);
        Logger.LogDebug("[SYNC] Cyberpunk2077Synchronizer.Synchronize END (pass 2)");
        return loadout;
    }
}
