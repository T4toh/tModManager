using FluentAssertions;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions.IndexSegments;
using NexusMods.MnemonicDB.Abstractions.Query;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.DependencyInjection;

namespace NexusMods.DataModel.SchemaVersions.Tests.MigrationSpecificTests;

/// <summary>
/// Test the migration to remove game files and add LocatorIds and GameVersions to Loadouts.
/// </summary>
/// <param name="provider"></param>
public class TestsFor_0004_RemoveGameFiles(ITestOutputHelper helper) : ALegacyDatabaseTest(helper)
{
    [Theory]
    [MethodData(nameof(DatabaseNames))]
    public async Task Test(string databaseName)
    {
        await using var tempConnection = await ConnectionFor(databaseName);
        var db = tempConnection.Connection.Db;
        var gameRegistry = tempConnection.Connection.ServiceProvider.GetRequiredService<IGameRegistry>();

        foreach (var loadout in Loadout.All(db))
        {
            // Loadouts for unregistered games (e.g. removed StardewValley) are skipped by migration
            if (!gameRegistry.TryGetGameInstallation(loadout, out _))
                continue;

            loadout.Contains(Loadout.LocatorIds).Should().BeTrue("Loadout should contain LocatorIds");
            loadout.Contains(Loadout.GameVersion).Should().BeTrue("Loadout should contain GameVersion");
        }

        var gameGroupAttrs = db.AttributeCache.AllAttributeIds
            .Where(sym => sym.Namespace == "NexusMods.Loadouts.LoadoutGameFilesGroup")
            .Select(sym => (sym, db.AttributeCache.GetAttributeId(sym)))
            .ToArray();
        
        foreach (var (sym, attrId) in gameGroupAttrs)
        {
            db.Datoms(SliceDescriptor.Create(attrId)).Should().BeEmpty($"All datoms with {sym} should be removed");
        }
    }
    
}
