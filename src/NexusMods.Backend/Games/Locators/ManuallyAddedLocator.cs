using System.Collections.Frozen;
using System.Collections.Immutable;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Backend.Games.Locators;

[UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
internal class ManuallyAddedLocator : IGameLocator
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnection _connection;
    private readonly IFileSystem _fileSystem;
    private readonly FrozenDictionary<GameId, IGameData> _registeredGames;

    public ManuallyAddedLocator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _fileSystem = serviceProvider.GetRequiredService<IFileSystem>();

        _registeredGames = serviceProvider
            .GetServices<IGameData>()
            .ToFrozenDictionary(x => x.GameId);
    }

    public IEnumerable<GameLocatorResult> Locate()
    {
        var entities = ManuallyAddedGame.All(_connection.Db);
        foreach (var entity in entities)
        {
            // An entry added before GameId existed whose Nexus id matched no registered game has none
            if (!ManuallyAddedGame.GameId.TryGetValue(entity, out var gameId) || !_registeredGames.TryGetValue(gameId, out var game)) continue;

            ILinuxCompatabilityDataProvider? linuxCompatProvider = null;
            if (entity.Contains(ManuallyAddedGame.WinePrefix) && !string.IsNullOrWhiteSpace(entity.WinePrefix))
            {
                // Declared by the stored path even when the folder is gone (deleted by hand, Lutris not launched yet),
                // like SteamLocator: the DB keeps WinePrefix paths and every sync needs the location
                var winePrefixPath = _fileSystem.FromUnsanitizedFullPath(entity.WinePrefix);
                linuxCompatProvider = new ManualLinuxCompatabilityDataProvider(winePrefixPath, _serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ManualLinuxCompatabilityDataProvider>>());
            }

            yield return new GameLocatorResult
            {
                StoreIdentifier = entity.Id.ToString(),
                Path = _fileSystem.FromUnsanitizedFullPath(entity.Path),
                LocatorIds = ImmutableArray<LocatorId>.Empty,
                Game = game,
                Store = GameStore.ManuallyAdded,
                Locator = this,
                LinuxCompatabilityDataProvider = linuxCompatProvider,
            };
        }
    }
}
