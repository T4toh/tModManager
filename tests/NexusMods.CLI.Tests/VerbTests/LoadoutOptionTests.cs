using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk;
using NexusMods.Sdk.Loadouts;
using NexusMods.SingleProcess;
using Text = NexusMods.Sdk.ProxyConsole.Text;

namespace NexusMods.CLI.Tests.VerbTests;

public class LoadoutOptionTests(IServiceProvider provider) : AVerbTest(provider)
{
    private readonly IServiceProvider _provider = provider;

    [Fact]
    public async Task IdPrintedByLoadoutsList_IsAcceptedByDashL()
    {
        var loadout = await CreateLoadout();

        var list = await Run("loadouts list");
        var id = list.LastTable.Rows
            .Select(r => ((Text)r[0]).Template)
            .Single(cell => cell == loadout.Id.Value.ToString("x"));

        await Run("loadout groups list", "-l", id);
    }

    [Fact]
    public async Task UnknownLoadout_FailsWithTheParserError()
    {
        var (code, output) = await RunRaw("loadout", "groups", "list", "-l", "no-such-loadout");

        code.Should().NotBe(0);
        output.Should().Contain("No loadout found with name or id no-such-loadout");
    }

    // ILoadoutManager.CreateLoadout syncs, which needs the vanilla file list this test setup has no hashes for;
    // the option parser only looks the loadout up
    private async Task<Loadout.ReadOnly> CreateLoadout()
    {
        var metadata = await _provider.GetRequiredService<ILoadoutManager>().ManageInstallation(await CreateInstall());
        var conn = _provider.GetRequiredService<IConnection>();
        using var tx = conn.BeginTransaction();
        var loadout = new Loadout.New(tx)
        {
            Name = "Loadout A",
            ShortName = "A",
            InstallationId = metadata,
            Revision = 0,
            LoadoutKind = LoadoutKind.Default,
            LocatorIds = [],
            GameVersion = VanityVersion.From("1.0"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout);
    }

    private async Task<(int Code, string Output)> RunRaw(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var code = await _provider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (code, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template)));
    }
}
