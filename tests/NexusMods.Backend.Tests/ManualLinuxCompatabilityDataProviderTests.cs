using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Backend;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Backend.Tests;

public class ManualLinuxCompatabilityDataProviderTests
{
    private const string UserReg = """
        [Software\\Wine\\DllOverrides] 1700000000
        "msvcp140"="native"
        "winmm"="builtin"
        """;

    private static (ManualLinuxCompatabilityDataProvider Provider, AbsolutePath Lutris) Setup(bool withLutrisConfig)
    {
        var tmp = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"manual-provider-{Guid.NewGuid():N}");
        var prefix = tmp.Combine("pfx");
        var lutris = tmp.Combine("lutris");
        prefix.CreateDirectory();
        File.WriteAllText(prefix.Combine("user.reg").ToString(), UserReg);

        var runnerWine = lutris.Combine("runners/wine/wine-ge-8-26-x86_64/bin/wine");
        runnerWine.Parent.CreateDirectory();
        File.WriteAllText(runnerWine.ToString(), "");

        if (withLutrisConfig)
        {
            var games = lutris.Combine("games");
            games.CreateDirectory();
            File.WriteAllText(games.Combine("cp-1.yml").ToString(), $"""
                game:
                  prefix: {prefix}
                wine:
                  overrides:
                    winmm: n,b
                  version: wine-ge-8-26-x86_64
                """);
        }

        return (new ManualLinuxCompatabilityDataProvider(prefix, lutris, NullLogger.Instance), lutris);
    }

    [Test]
    public async Task RuntimeIsTheLutrisWineBuild()
    {
        var (provider, lutris) = Setup(withLutrisConfig: true);

        var wine = await Assert.That(provider.Runtime).IsTypeOf<WineRuntime.Wine>();
        await Assert.That(wine!.RunnerDirectory).IsEqualTo(lutris.Combine("runners/wine/wine-ge-8-26-x86_64"));
        await Assert.That(wine.Environment["WINEDLLOVERRIDES"]).IsEqualTo("winmm=n,b");
    }

    [Test]
    public async Task RuntimeIsNullWithoutALutrisConfig()
    {
        var (provider, _) = Setup(withLutrisConfig: false);
        await Assert.That(provider.Runtime).IsNull();
    }

    [Test]
    public async Task OverridesUnionRegistryAndLutris_LutrisWins()
    {
        var (provider, _) = Setup(withLutrisConfig: true);

        var overrides = await provider.GetWineDllOverrides(CancellationToken.None);

        await Assert.That(overrides.Select(o => o.ToString()).Order().ToArray()).IsEquivalentTo(new[] { "msvcp140=n", "winmm=n,b" });
    }
}
