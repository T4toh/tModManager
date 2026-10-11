using System.Collections.Immutable;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.FileHashes.Emitters;
using NexusMods.Games.FOMOD;
using NexusMods.Games.RedEngine.Cyberpunk2077.Emitters;
using NexusMods.Games.RedEngine.Cyberpunk2077.SortOrder;
using NexusMods.Games.RedEngine.ModInstallers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;

namespace NexusMods.Games.RedEngine.Cyberpunk2077;

[UsedImplicitly]
public class Cyberpunk2077Game : IGame, IGameData<Cyberpunk2077Game>, IDisposable
{
    public static GameId GameId { get; } = GameId.From("RedEngine.Cyberpunk2077");
    public static string DisplayName => "Cyberpunk 2077";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(3333);

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [1091500u],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<Cyberpunk2077Game>("NexusMods.Games.RedEngine.Resources.Cyberpunk2077.thumbnail.webp");
    public IStreamFactory TileImage { get; } = new EmbeddedResourceStreamFactory<Cyberpunk2077Game>("NexusMods.Games.RedEngine.Resources.Cyberpunk2077.tile.webp");

    private readonly Lazy<ILoadoutSynchronizer> _synchronizer;
    public ILoadoutSynchronizer Synchronizer => _synchronizer.Value;
    public ILibraryItemInstaller[] LibraryItemInstallers { get; }
    private readonly Lazy<ISortOrderManager> _sortOrderManager;
    public ISortOrderManager SortOrderManager => _sortOrderManager.Value;
    public IDiagnosticEmitter[] DiagnosticEmitters { get; }

    public void Dispose()
    {
        if (_sortOrderManager.IsValueCreated) (_sortOrderManager.Value as IDisposable)?.Dispose();
    }

    public Cyberpunk2077Game(IServiceProvider provider)
    {
        _synchronizer = new Lazy<ILoadoutSynchronizer>(() => new Cyberpunk2077Synchronizer(provider));
        _sortOrderManager = new Lazy<ISortOrderManager>(() =>
        {
            // One per game: RegisterSortOrderVarieties replaces whatever varieties the instance had.
            var sortOrderManager = new SortOrderManager(provider);
            sortOrderManager.RegisterSortOrderVarieties(
                sortOrderVarieties: [
                    provider.GetRequiredService<RedModSortOrderVariety>(),
                ],
                game: this
            );

            return sortOrderManager;
        });

        DiagnosticEmitters =
        [
            new UndeployableLoadoutDueToMissingGameFiles(provider),
            new PatternBasedDependencyEmitter(PatternDefinitions.Definitions, provider),
            new MissingProtontricksForRedModEmitter(provider),
            new MissingRedModEmitter(),
            new CoreModsDiagnosticEmitter(),
            new WinePrefixRequirementsEmitter(),
        ];

        LibraryItemInstallers =
        [
            FomodXmlInstaller.Create(provider, new GamePath(LocationId.Game, "")),
            new RedModInstaller(provider),
            new SimpleOverlayModInstaller(provider),
            new AppearancePresetInstaller(provider),
            new FolderlessModInstaller(provider),
        ];
    }

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        var locations = new Dictionary<LocationId, AbsolutePath>
        {
            { LocationId.Game, gameLocatorResult.Path },
            // Saves stay out until a mod needs them: they would go through the same whitelist as the settings
        };
        // The prefix root, not the settings folder: SafePath.IsUnderSymlink from the root covers every folder in
        // between (Wine links Documents/Desktop to $HOME; people link the saves folder to a sync folder)
        if (gameLocatorResult.LinuxCompatabilityDataProvider is { } linux)
            locations[LocationId.WinePrefix] = linux.WinePrefixDirectoryPath;
        return locations.ToImmutableDictionary();
    }

    public ImmutableDictionary<LocationId, ImmutableHashSet<RelativePath>> GetManagedFiles(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        if (gameLocatorResult.LinuxCompatabilityDataProvider is not { } linux)
            return ImmutableDictionary<LocationId, ImmutableHashSet<RelativePath>>.Empty;

        return ImmutableDictionary<LocationId, ImmutableHashSet<RelativePath>>.Empty.Add(LocationId.WinePrefix, [UserSettingsPath(linux.WinePrefixDirectoryPath)]);
    }

    /// <summary>
    /// UserSettings.json inside the prefix, relative to the prefix root. One helper for the whitelist and for the
    /// intrinsic file, so both always name the same path (Proton: steamuser; Wine/Lutris: the real user).
    /// </summary>
    public static RelativePath UserSettingsPath(AbsolutePath prefix) =>
        (RelativePath)$"drive_c/users/{WineUserName(prefix)}/AppData/Local/CD Projekt Red/Cyberpunk 2077/UserSettings.json";

    /// <summary>
    /// Proton runs every game as <c>steamuser</c>; a Wine/Lutris prefix uses the real user name. Before the first
    /// launch neither folder exists and the Proton name is assumed.
    /// </summary>
    private static string WineUserName(AbsolutePath prefix)
    {
        var users = prefix.Combine("drive_c/users");
        if (users.Combine("steamuser").DirectoryExists()) return "steamuser";
        var current = Environment.UserName;
        return users.Combine(current).DirectoryExists() ? current : "steamuser";
    }

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "bin/x64/Cyberpunk2077.exe");
}
