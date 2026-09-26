using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using GameFinder.StoreHandlers.Steam;
using GameFinder.StoreHandlers.Steam.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.Backend.Games.Locators;

internal class SteamLocator : IGameLocator
{
    private readonly ILogger _logger;
    private readonly SteamHandler _handler;
    private readonly FrozenDictionary<uint, IGameData> _registeredGames;

    private static readonly GameStore Store = GameStore.Steam;

    public SteamLocator(IEnumerable<IGameData> games, ILoggerFactory loggerFactory, IFileSystem fileSystem, GameFinder.RegistryUtils.IRegistry? registry)
    {
        _logger = loggerFactory.CreateLogger<SteamLocator>();

        var handlerLogger = loggerFactory.CreateLogger<SteamHandler>();
        if (OSInformation.Shared.IsLinux)
        {
            if (!GetCommonSteamPaths().Any(p => Directory.Exists(p) && File.Exists(Path.Combine(p, "config/libraryfolders.vdf"))))
            {
                handlerLogger = NullLogger<SteamHandler>.Instance;
            }
        }

        _handler = new SteamHandler(
            fileSystem: fileSystem,
            registry: registry,
            logger: handlerLogger
        );

        _registeredGames = games
            .SelectMany(game => game.StoreIdentifiers.SteamAppIds, (game, storeIdentifier) => new KeyValuePair<uint, IGameData>(storeIdentifier, game))
            .ToFrozenDictionary();
    }

    private static string[] GetCommonSteamPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            Path.Combine(home, ".local/share/Steam"),
            Path.Combine(home, ".steam/steam"),
            Path.Combine(home, ".steam/debian-installation"),
            Path.Combine(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam")
        ];
    }

    public IEnumerable<GameLocatorResult> Locate()
    {
        // NOTE(erri120): silences warnings if Steam isn't installed
        if (OSInformation.Shared.IsLinux)
        {
            var steamExists = GetCommonSteamPaths().Any(p => 
                Directory.Exists(p) && 
                File.Exists(Path.Combine(p, "config/libraryfolders.vdf")));
            
            if (!steamExists) yield break;
        }

        foreach (var result in _handler.FindAllGames())
        {
            if (result.TryPickT1(out var errorMessage, out var gameFinderGame))
            {
                _logger.LogWarning("Error locating games: {ErrorMessage}", errorMessage.Message);
                continue;
            }

            var storeIdentifier = gameFinderGame.AppId.Value;
            _logger.LogDebug("Found game '{GameName}' with store identifier '{StoreIdentifier}'", gameFinderGame.Name, storeIdentifier);

            if (!_registeredGames.TryGetValue(storeIdentifier, out var game)) continue;

            var path = gameFinderGame.Path;

            var locatorIds = gameFinderGame.AppManifest.InstalledDepots
                .Select(x => LocatorId.From(x.Value.ManifestId.Value.ToString()))
                .ToImmutableArray();

            var winePrefixDirectoryPath = gameFinderGame.GetProtonPrefix()?.ProtonDirectory.Combine("pfx");
            var linuxCompatibilityDataProvider = winePrefixDirectoryPath is not null ? new LinuxCompatibilityDataProvider(gameFinderGame, winePrefixDirectoryPath.Value) : null;

            var platform = linuxCompatibilityDataProvider is null ? OSInformation.Shared.Platform : OSPlatform.Windows;

            yield return new GameLocatorResult
            {
                Game = game,
                Path = path,
                LocatorIds = locatorIds,
                Platform = platform,
                StoreIdentifier = storeIdentifier.ToString(),
                Store = Store,
                Locator = this,
                // Feeds the Wine prefix health check (DLL overrides, winetricks packages) and the prefix status panel
                LinuxCompatabilityDataProvider = linuxCompatibilityDataProvider,
            };
        }
    }

    private class LinuxCompatibilityDataProvider : ILinuxCompatabilityDataProvider
    {
        private readonly SteamGame _steamGame;

        public AbsolutePath WinePrefixDirectoryPath { get; }

        public LinuxCompatibilityDataProvider(SteamGame steamGame, AbsolutePath winePrefixDirectoryPath)
        {
            _steamGame = steamGame;
            WinePrefixDirectoryPath = winePrefixDirectoryPath;
        }

        public ValueTask<ImmutableHashSet<string>> GetInstalledWinetricksComponents(CancellationToken cancellationToken)
        {
            var filePath = WineParser.GetWinetricksLogFilePath(WinePrefixDirectoryPath);
            var result = WineParser.ParseWinetricksLogFile(filePath);
            return new ValueTask<ImmutableHashSet<string>>(result);
        }

        public ValueTask<ImmutableArray<WineDllOverride>> GetWineDllOverrides(CancellationToken cancellationToken)
        {
            var localConfigPath = SteamLocationFinder.GetUserDataDirectoryPath(_steamGame.SteamPath, _steamGame.AppManifest.LastOwner).Combine("config").Combine("localconfig.vdf");
            var parserResult = LocalUserConfigParser.ParseConfigFile(_steamGame.AppManifest.LastOwner, localConfigPath);
            if (parserResult.IsFailed || !parserResult.Value.LocalAppData.TryGetValue(_steamGame.AppId, out var localAppData))
                return new ValueTask<ImmutableArray<WineDllOverride>>(ImmutableArray<WineDllOverride>.Empty);

            var launchOptions = localAppData.LaunchOptions;
            var section = WineParser.GetWineDllOverridesSection(launchOptions);
            var result = WineParser.ParseEnvironmentVariable(section);
            return new ValueTask<ImmutableArray<WineDllOverride>>(result);
        }
    }
}
