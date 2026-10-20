namespace NexusMods.Sdk.Tests;

public class WineParserRegistryTests
{
    // Exactly as Wine writes user.reg: doubled backslashes in the key, a #time line, word-form values.
    private const string UserReg = """
        WINE REGISTRY Version 2
        ;; All keys relative to \\User\\S-1-5-21-0-0-0-1000

        [Software\\Wine\\DllOverrides] 1700000000
        #time=1dd543fb601cb00
        "d3d11"=""
        "msvcp140"="native"
        "winmm"="native,builtin"

        [Software\\Wine\\Fonts] 1700000000
        "Codepages"="1252,437"
        """;

    [Test]
    public async Task ReadsTheRealUserRegFormat()
    {
        var overrides = WineParser.ParseDllOverridesFromRegistry(UserReg);
        await Assert.That(overrides.Select(o => o.ToString()).Order().ToArray()).IsEquivalentTo(new[] { "d3d11=", "msvcp140=n", "winmm=n,b" });
    }

    [Test]
    public async Task NothingWithoutTheSection()
    {
        await Assert.That(WineParser.ParseDllOverridesFromRegistry("[Software\\\\Wine\\\\Fonts] 1\n\"Codepages\"=\"1252\"\n").IsEmpty).IsTrue();
    }
}
