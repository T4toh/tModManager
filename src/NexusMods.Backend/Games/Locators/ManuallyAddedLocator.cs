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
    private readonly FrozenDictionary<NexusModsGameId, IGameData> _registeredGames;

    public ManuallyAddedLocator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _fileSystem = serviceProvider.GetRequiredService<IFileSystem>();

        _registeredGames = serviceProvider
            .GetServices<IGameData>()
            .Where(x => x.NexusModsGameId.HasValue)
            .Select(x => new KeyValuePair<NexusModsGameId, IGameData>(x.NexusModsGameId.Value, x))
            .ToFrozenDictionary();
    }

    public IEnumerable<GameLocatorResult> Locate()
    {
        var entities = ManuallyAddedGame.All(_connection.Db);
        foreach (var entity in entities)
        {
            if (!_registeredGames.TryGetValue(entity.GameId, out var game)) continue;

            ILinuxCompatabilityDataProvider? linuxCompatProvider = null;
            if (entity.Contains(ManuallyAddedGame.WinePrefix) && !string.IsNullOrWhiteSpace(entity.WinePrefix))
            {
                var winePrefixPath = _fileSystem.FromUnsanitizedFullPath(entity.WinePrefix);
                if (winePrefixPath.DirectoryExists())
                {
                    linuxCompatProvider = new ManualLinuxCompatabilityDataProvider(winePrefixPath, _serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ManualLinuxCompatabilityDataProvider>>());
                }
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
