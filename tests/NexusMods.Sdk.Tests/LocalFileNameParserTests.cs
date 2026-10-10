using NexusMods.Sdk.Library;

namespace NexusMods.Sdk.Tests;

public class LocalFileNameParserTests
{
    [Test]
    [Arguments("Mod-1.2.3.zip", "1.2.3")]
    [Arguments("Mod_v1.2.zip", "1.2")]
    [Arguments("Mod v2.0.1 (fixed).7z", "2.0.1")]
    [Arguments("Mod V3.zip", "3")]
    [Arguments("CET-1.35.1.zip", "1.35.1")]
    [Arguments("mod 0.9.zip", "0.9")]
    public async Task TryParseVersion_FindsTheVersion(string fileName, string expected)
    {
        await Assert.That(LocalFileNameParser.TryParseVersion(fileName)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("Mod.zip")]
    [Arguments("Mod-123-1-0-1757.zip")] // Nexus naming: mod id + version with dashes + timestamp; not guessed
    [Arguments("2077.zip")]
    [Arguments("")]
    public async Task TryParseVersion_LeavesUnknownAlone(string fileName)
    {
        await Assert.That(LocalFileNameParser.TryParseVersion(fileName)).IsNull();
    }

    [Test]
    [Arguments("Mod-1.2.3.zip", "Mod-1.2.3")]
    [Arguments("archive.tar", "archive")]
    [Arguments("noext", "noext")]
    public async Task DisplayName_DropsTheExtension(string fileName, string expected)
    {
        await Assert.That(LocalFileNameParser.DisplayName(fileName)).IsEqualTo(expected);
    }
}
