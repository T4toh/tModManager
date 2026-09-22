using NexusMods.Paths;

namespace NexusMods.Sdk.Tests;

public class DataDirectoryMigrationTests
{
    [Test]
    public async Task MigrateLegacyDataDirectory_MovesOnce()
    {
        var fs = FileSystem.Shared;
        var basePath = fs.GetKnownPath(KnownPath.TempDirectory).Combine($"tModManager-migration-{Guid.NewGuid()}");
        var legacy = basePath.Combine(ApplicationConstants.LegacyDataDirectoryName);
        var current = basePath.Combine(ApplicationConstants.DataDirectoryName);
        try
        {
            legacy.Combine("DataModel").CreateDirectory();
            File.WriteAllText(legacy.Combine("DataModel/marker.txt").ToString(), "hi");

            await Assert.That(DataDirectoryMigration.MigrateLegacyDataDirectory(basePath)).IsTrue();
            await Assert.That(legacy.DirectoryExists()).IsFalse();
            await Assert.That(File.ReadAllText(current.Combine("DataModel/marker.txt").ToString())).IsEqualTo("hi");

            // Second run: new directory exists, nothing moves, data untouched.
            legacy.CreateDirectory();
            await Assert.That(DataDirectoryMigration.MigrateLegacyDataDirectory(basePath)).IsFalse();
            await Assert.That(current.Combine("DataModel/marker.txt").FileExists).IsTrue();
        }
        finally
        {
            if (basePath.DirectoryExists()) basePath.DeleteDirectory(recursive: true);
        }
    }
}
