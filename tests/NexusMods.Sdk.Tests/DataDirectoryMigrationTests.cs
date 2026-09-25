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

    [Test]
    public async Task CopyUpstreamDataOnce_CopiesConfigsAndHashesWithoutUpstreamPaths()
    {
        var basePath = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"tModManager-upstream-{Guid.NewGuid()}");
        var upstream = basePath.Combine(ApplicationConstants.UpstreamDirectoryName);
        var ours = basePath.Combine(ApplicationConstants.DataDirectoryName);
        try
        {
            upstream.Combine("Configs").CreateDirectory();
            upstream.Combine("FileHashesDatabase/db").CreateDirectory();
            File.WriteAllText(upstream.Combine("Configs/Downloads.json").ToString(), """{"Folder":"tModManager/Downloads"}""");
            File.WriteAllText(upstream.Combine("Configs/Logging.json").ToString(), """{"File":"NexusMods.App/Logs/x.log"}""");
            File.WriteAllText(upstream.Combine("FileHashesDatabase/db/data").ToString(), "hashes");

            DataDirectoryMigration.CopyUpstreamDataOnce(basePath);

            await Assert.That(ours.Combine("Configs/Downloads.json").FileExists).IsTrue();
            await Assert.That(ours.Combine("Configs/Logging.json").FileExists).IsFalse();
            await Assert.That(File.ReadAllText(ours.Combine("FileHashesDatabase/db/data").ToString())).IsEqualTo("hashes");
            // Copied, not moved: the official app keeps its folder
            await Assert.That(upstream.Combine("Configs/Logging.json").FileExists).IsTrue();

            // Second run never overwrites what tModManager has since written
            File.WriteAllText(ours.Combine("Configs/Downloads.json").ToString(), "changed");
            DataDirectoryMigration.CopyUpstreamDataOnce(basePath);
            await Assert.That(File.ReadAllText(ours.Combine("Configs/Downloads.json").ToString())).IsEqualTo("changed");
        }
        finally
        {
            if (basePath.DirectoryExists()) Directory.Delete(basePath.ToString(), recursive: true);
        }
    }
}
