using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Cli;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Sdk.Hashes;
using NexusMods.Abstractions.Steam.DTOs;
using NexusMods.Abstractions.Steam.Values;
using NexusMods.Games.FileHashes.DTOs;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.MnemonicDB;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Storage;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.ProxyConsole;
using YamlDotNet.Serialization;
using Manifest = NexusMods.Abstractions.Steam.DTOs.Manifest;
using OperatingSystem = NexusMods.Abstractions.Games.FileHashes.Values.OperatingSystem;

namespace NexusMods.Games.FileHashes.VerbImpls;

// Format is a list of (Game, (OS, (Version, VersionDefinition)))
using VersionFileFormat = Dictionary<string, Dictionary<string, VersionContribDefinition[]>>;

public class BuildHashesDb : IAsyncDisposable
{
    private readonly TemporaryPath _tempFolder;
    private readonly DatomStore _datomStore;
    private readonly Connection _connection;
    private readonly IRenderer _renderer;
    private readonly JsonSerializerOptions _jsonOptions;
    
    private readonly Dictionary<(RelativePath Path, EntityId HashId), EntityId> _knownHashPaths = new();
    private readonly MnemonicDB.Storage.RocksDbBackend.Backend _backend;
    private readonly IGameRegistry _gameRegistry;

    public BuildHashesDb(IRenderer renderer, IServiceProvider provider, TemporaryFileManager temporaryFileManager, IGameRegistry gameRegistry)
    {
        Provider = provider;
        _renderer = renderer;
        _gameRegistry = gameRegistry;
        _tempFolder = temporaryFileManager.CreateFolder();
        _jsonOptions = provider.GetRequiredService<JsonSerializerOptions>();
        
        
        
        _backend = new MnemonicDB.Storage.RocksDbBackend.Backend();
        var settings = new DatomStoreSettings
        {
            Path = _tempFolder,
        };
        _datomStore = new DatomStore(provider.GetRequiredService<ILogger<DatomStore>>(), settings, _backend);
        _connection = new Connection(provider.GetRequiredService<ILogger<Connection>>(), _datomStore, provider, []);
    }
    
    public IServiceProvider Provider { get; set; }

    public async Task BuildFrom(AbsolutePath path, AbsolutePath output)
    {
        try
        {
            await AddHashes(path);
            await AddSteamData(path);
            await AddVersions(path);
        }
        catch (Exception ex)
        {
            await _renderer.Error(ex, "Failed to build database");
            throw;
        }

        await _renderer.TextLine("Exporting database");
        await _connection.FlushAndCompact();
        
        _datomStore.Dispose();
        _backend.Dispose();
        
        if (output.FileExists)
            output.Delete();
        
        var hashesDbPath = output / "game_hashes_db.zip";
        if (hashesDbPath.FileExists)
            hashesDbPath.Delete();
        
        var manifestPath = output / "manifest.json";
        ZipFile.CreateFromDirectory(_tempFolder.Path.ToString(), hashesDbPath.ToString(), CompressionLevel.SmallestSize, false);

        var zipHash = await hashesDbPath.XxHash3Async();

        var manifest = new DTOs.Manifest()
        {
            CreatedAt = DateTimeOffset.UtcNow,
            Assets =
            [
                new DTOs.Asset()
                {
                    Type = AssetType.GameHashDb,
                    Hash = zipHash,
                    Size = hashesDbPath.FileInfo.Size,
                },
            ],
        };

        await using var manifestStream = manifestPath.Create();
        await JsonSerializer.SerializeAsync(manifestStream, manifest, _jsonOptions);

        await _renderer.TextLine("Database built and exported to {0}. Final size {1}. Hash: {2}", output, hashesDbPath.FileInfo.Size, zipHash);
    }

    private async Task AddVersions(AbsolutePath path)
    {
        var versionsPath = path / "contrib/version_aliases.yaml";
        await using var versionsStream = versionsPath.Read();
        var versions = new DeserializerBuilder()
            .Build()
            .Deserialize<VersionFileFormat>(new StreamReader(versionsStream));

        var versionData = (from game in versions
            let gameName = game.Key
            from os in game.Value
            let osName = os.Key
            from definition in os.Value
            select (gameName, osName, definition)).ToArray();
        
        await _renderer.TextLine("Found {0} version mappings", versionData.Length);
        
        var referenceDb = _connection.Db;
        using var tx = _connection.BeginTransaction();
        foreach (var (gameName, osName, definition) in versionData)
        {
            var gameObject = Provider.GetServices<IGameData>().First(g => g.DisplayName == gameName);

            var os = osName switch
            {
                "Windows" => OperatingSystem.Windows,
                "MacOS" => OperatingSystem.MacOS,
                "Linux" => OperatingSystem.Linux,
                _ => throw new Exception("Unknown OS"),
            };

            var versionDef = new VersionDefinition.New(tx)
            {
                Name = definition.Name,
                OperatingSystem = os,
                GameId = gameObject.NexusModsGameId.Value,
                Steam = definition.Steam ?? [],
            };

            foreach (var id in definition.Steam ?? [])
            {
                try
                {
                    var manifest = SteamManifest
                        .FindByManifestId(referenceDb, ManifestId.From(ulong.Parse(id)))
                        .Single();
                    tx.Add(versionDef, VersionDefinition.SteamManifestsIds, manifest.Id);
                }
                catch (InvalidOperationException)
                {
                    await _renderer.TextLine("Failed to anchor Steam manifest {0} to {1} for {2}", id, definition.Name, gameName);
                }
            }
            
        }

        await tx.Commit();
    }

    private async Task AddSteamData(AbsolutePath path)
    {
        var refDb = _connection.Db;
        using var tx = _connection.BeginTransaction();
        await _renderer.TextLine("Importing Steam data");
        
        var appPath = path / "json" / "stores" / "steam" / "apps";

        var manifestCount = 0;
        var pathCounts = 0;
        
        var manifests = await LoadAllFromFolder<Manifest>(path / "json" / "stores" / "steam" / "manifests");
        
        var apps = await LoadAllFromFolder<ProductInfo>(path / "json" / "stores" / "steam" / "apps");
        
        var depotsToAppId = (from app in apps.Values
            from depot in app.Depots
            select (depot.DepotId, app.AppId))
            .ToLookup(t => t.DepotId, t => t.AppId);
        

        foreach (var (manifestName, manifestInfo) in manifests)
        {
            var manifestPath = path / "json" / "stores" / "steam" / "manifests" / (manifestInfo.ManifestId + ".json");
            await using var manifestStream = manifestPath.Read();
            var parsedManifest = (await JsonSerializer.DeserializeAsync<Manifest>(manifestStream, _jsonOptions))!;

            var pathIds = new List<EntityId>();
            bool hasFailures = false;
            foreach (var file in parsedManifest.Files)
            {
                // Steam manifests include folders, which we don't care about
                if (file.Chunks.Length == 0)
                    continue;

                var refDbRelation = HashRelation.FindBySha1(refDb, file.Hash).FirstOrDefault();
                if (!refDbRelation.IsValid())
                {
                    await _renderer.TextLine($"Skipping {0} due to missing SHA1 hashes", manifestInfo.ManifestId);
                    hasFailures = true;
                    break;
                }

                // The paths from steam can be in a format that isn't directly valid, so we sanitize them
                var sanitizedPath = RelativePath.FromUnsanitizedInput(file.Path.ToString());
                var relationId = EnsureHashPathRelation(tx, refDb, sanitizedPath,
                    file.Hash
                );
                pathIds.Add(relationId);
                pathCounts++;
            }

            if (hasFailures)
            {
                continue;
            }
            
            if (!depotsToAppId[manifestInfo.DepotId].TryGetFirst(out var appId))
            {
                await _renderer.TextLine("Skipping Manifest {0} because it doesn't have a matching AppId", manifestInfo.ManifestId);
                continue;
            }

            _ = new SteamManifest.New(tx)
            {
                AppId = appId,
                DepotId = manifestInfo.DepotId,
                ManifestId = manifestInfo.ManifestId,
                Name = manifestName,
                FilesIds = pathIds,
            };
            manifestCount++;
        }

        var result = await tx.Commit();
        RemapHashPaths(result);
        await _renderer.TextLine("Imported {0} manifests with {1} paths", manifestCount, pathCounts);
    }

    private async Task<Dictionary<string, T>> LoadAllFromFolder<T>(AbsolutePath folder)
    {
        var dict = new Dictionary<string, T>();
        foreach (var file in folder.EnumerateFiles(KnownExtensions.Json))
        {
            try
            {
                await using var fs = file.Read();
                var parsed = JsonSerializer.Deserialize<T>(fs, _jsonOptions)!;
                dict[file.GetFileNameWithoutExtension()] = parsed;
            }
            catch (Exception ex)
            {
                await _renderer.Error(ex, "Failed to load {0}: {1}", file, ex.Message);
            }
        }

        return dict;
    }

    private void RemapHashPaths(ICommitResult result)
    {
        var toRemap = _knownHashPaths.Where(f => f.Value.Partition == PartitionId.Temp).ToArray();
        
        foreach (var (key, id) in toRemap)
        {
            _knownHashPaths[key] = result[id];
        }
    }

    private async Task AddHashes(AbsolutePath path)
    {
        var hashPath = path / "json" / "hashes";

        await _renderer.TextLine("Importing hashes");
        using var tx = _connection.BeginTransaction();
        
        var count = 0;
        foreach (var file in hashPath.EnumerateFiles(KnownExtensions.Json))
        {
            try
            {
                await using var fs = file.Read();
                var parsedHash = (await JsonSerializer.DeserializeAsync<MultiHash>(fs, _jsonOptions))!;

                _ = new HashRelation.New(tx)
                {
                    XxHash3 = parsedHash.XxHash3,
                    XxHash64 = parsedHash.XxHash64,
                    MinimalHash = parsedHash.MinimalHash,
                    Md5 = parsedHash.Md5,
                    Sha1 = parsedHash.Sha1,
                    Crc32 = parsedHash.Crc32,
                    Size = parsedHash.Size,
                };
                count++;
            }
            catch (Exception ex)
            {
                await _renderer.Error(ex, "Failed to import {0}: {1}", file, ex.Message);
            }
        }

        await tx.Commit();
        await _renderer.TextLine("Imported {0} hashes", count);
    }

    /// <summary>
    /// Find or insert a hash path relation
    /// </summary>
    private EntityId EnsureHashPathRelation(ITransaction tx, IDb referenceDb, RelativePath path, Hash hash)
    {
        var hashRelation = HashRelation.FindByXxHash3(referenceDb, hash).FirstOrDefault();
        
        if (!hashRelation.IsValid())
        {
            throw new Exception("Hash not found in the reference database");
        }
        
        return EnsureHashPathRelation(tx, path, hashRelation);
    }
    
    /// <summary>
    /// Find or insert a hash path relation
    /// </summary>
    private EntityId EnsureHashPathRelation(ITransaction tx, IDb referenceDb, RelativePath path, Sha1Value hash)
    {
        var hashRelation = HashRelation.FindBySha1(referenceDb, hash).FirstOrDefault();
        
        if (!hashRelation.IsValid())
        {
            throw new Exception("Hash not found in the reference database");
        }
        
        return EnsureHashPathRelation(tx, path, hashRelation);
    }

    private EntityId EnsureHashPathRelation(ITransaction tx, RelativePath path, HashRelationId hashRelation)
    {
        if (_knownHashPaths.TryGetValue((path, hashRelation), out var entityId))
        {
            return entityId;
        }
        
        var newHashPathRelation = new PathHashRelation.New(tx)
        {
            Path = path,
            HashId = hashRelation,
        };
        
        _knownHashPaths[(path, hashRelation)] = newHashPathRelation.Id;
        
        return newHashPathRelation.Id;
    }

    public async ValueTask DisposeAsync()
    {
        _datomStore.Dispose();
        await _tempFolder.DisposeAsync();
    }
}
