using NexusMods.Paths;

namespace NexusMods.Sdk.Tests;

public class TestIsolationTests
{
    [Test]
    [Arguments(KnownPath.XDG_DATA_HOME)]
    [Arguments(KnownPath.XDG_STATE_HOME)]
    [Arguments(KnownPath.XDG_CONFIG_HOME)]
    [Arguments(KnownPath.XDG_CACHE_HOME)]
    public async Task XdgFoldersPointAtTheTestRunFolder(KnownPath knownPath)
    {
        var expectedRoot = Path.Combine(Path.GetTempPath(), $"tmm-tests-{Environment.ProcessId}");
        await Assert.That(FileSystem.Shared.GetKnownPath(knownPath).ToString()).StartsWith(expectedRoot);
    }
}
