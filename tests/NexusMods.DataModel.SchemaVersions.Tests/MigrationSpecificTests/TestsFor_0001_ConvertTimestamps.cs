
using FluentAssertions;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.BuiltInEntities;
using Xunit;

namespace NexusMods.DataModel.SchemaVersions.Tests.MigrationSpecificTests;

public class TestsFor_0001_ConvertTimestamps(ITestOutputHelper helper) : ALegacyDatabaseTest(helper)
{
    [Fact]
    public async Task OldTimestampsAreInRange()
    {
        await using var tempConnection = await ConnectionFor("Migration-5.rocksdb.zip");

        var txTimes = tempConnection.Connection.Db.Datoms(Transaction.Timestamp)
            .Resolved(tempConnection.Connection)
            .OfType<TimestampAttribute.ReadDatom>()
            .Select(d => d.V.Year)
            .ToArray();
        
        txTimes.Should().NotBeEmpty();

        foreach (var year in txTimes)
        {
            year.Should().BeGreaterOrEqualTo(2024).And.BeLessOrEqualTo(DateTimeOffset.UtcNow.Year);
        }
    }
}
