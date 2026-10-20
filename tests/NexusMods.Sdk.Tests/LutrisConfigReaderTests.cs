using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Sdk.Tests;

public class LutrisConfigReaderTests
{
    private const string WineGe = """
        game:
          exe: /home/u/Juegos/cp2077-manual/bin/x64/Cyberpunk2077.exe
          prefix: /home/u/Juegos/pfx-cp/
        system:
          env:
            UMU_RUNTIME_UPDATE: '0'
            WINEDLLOVERRIDES: version=n,b;winmm=b
          gamemode: false
          prefix_command: ''
        wine:
          overrides:
            winmm: n,b
          version: wine-ge-8-26-x86_64
        """;

    private const string GeProton = """
        game:
          exe: /mnt/Datos/Wine/gog/kotor/drive_c/GOG Games/Star Wars - KotOR/swkotor.exe
          prefix: /mnt/Datos/Wine/gog/kotor
        system:
          env:
            MESA_LOADER_DRIVER_OVERRIDE: zink
          prefix_command: ''
        wine:
          version: GE-Proton7-55
        """;

    private const string NoPrefixNoVersion = """
        game:
          exe: ''
        system:
          env:
            UMU_RUNTIME_UPDATE: '0'
          gamemode: false
          prefix_command: ''
        wine:
          version: wine-ge-8-26-x86_64
        """;

    private const string SteamShape = """
        system: {}
        null: {}
        game:
          appid: '431960'
        """;

    private static AbsolutePath TempDir()
    {
        var dir = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"lutris-tests-{Guid.NewGuid():N}");
        dir.CreateDirectory();
        return dir;
    }

    private static void Touch(AbsolutePath file)
    {
        file.Parent.CreateDirectory();
        File.WriteAllText(file.ToString(), "");
    }

    [Test]
    public async Task ParsesPrefixExeVersionAndEnv()
    {
        var cfg = LutrisConfigReader.Parse(WineGe);
        await Assert.That(cfg.Prefix).IsEqualTo("/home/u/Juegos/pfx-cp/");
        await Assert.That(cfg.Exe).EndsWith("Cyberpunk2077.exe");
        await Assert.That(cfg.WineVersion).IsEqualTo("wine-ge-8-26-x86_64");
        await Assert.That(cfg.Env["UMU_RUNTIME_UPDATE"]).IsEqualTo("0");
        await Assert.That(cfg.Env.ContainsKey("WINEDLLOVERRIDES")).IsFalse();
    }

    [Test]
    public async Task OverridesUnionWithWineOverridesWinning()
    {
        var cfg = LutrisConfigReader.Parse(WineGe);
        await Assert.That(cfg.Overrides.Select(o => o.ToString()).Order().ToArray()).IsEquivalentTo(new[] { "version=n,b", "winmm=n,b" });
    }

    [Test]
    public async Task BuildEnvironmentAddsWineDllOverrides()
    {
        var env = LutrisConfigReader.BuildEnvironment(LutrisConfigReader.Parse(WineGe));
        await Assert.That(env["UMU_RUNTIME_UPDATE"]).IsEqualTo("0");
        await Assert.That(env["WINEDLLOVERRIDES"].Split(';').Order().ToArray()).IsEquivalentTo(new[] { "version=n,b", "winmm=n,b" });
    }

    [Test]
    public async Task SteamShapeParsesToEmptyConfig()
    {
        var cfg = LutrisConfigReader.Parse(SteamShape);
        await Assert.That(cfg.Prefix).IsNull();
        await Assert.That(cfg.WineVersion).IsNull();
        await Assert.That(cfg.Env.Count).IsEqualTo(0);
        await Assert.That(cfg.Overrides.IsEmpty).IsTrue();
    }

    [Test]
    public async Task FindByPrefixMatchesWithTrailingSlashAndCase()
    {
        var tmp = TempDir();
        var yml = tmp.Combine("games/cp-1.yml");
        yml.Parent.CreateDirectory();
        File.WriteAllText(yml.ToString(), WineGe);

        await Assert.That(LutrisConfigReader.FindByPrefix(tmp, FileSystem.Shared.FromUnsanitizedFullPath("/home/u/Juegos/pfx-cp"))).IsNotNull();
        await Assert.That(LutrisConfigReader.FindByPrefix(tmp, FileSystem.Shared.FromUnsanitizedFullPath("/home/u/Juegos/PFX-CP"))).IsNotNull();
        await Assert.That(LutrisConfigReader.FindByPrefix(tmp, FileSystem.Shared.FromUnsanitizedFullPath("/home/u/other"))).IsNull();
    }

    [Test]
    public async Task ResolvesWineBuild()
    {
        var tmp = TempDir();
        Touch(tmp.Combine("runners/wine/wine-ge-8-26-x86_64/bin/wine"));

        var runtime = LutrisConfigReader.ResolveRuntime(LutrisConfigReader.Parse(WineGe), tmp);

        var wine = await Assert.That(runtime).IsTypeOf<WineRuntime.Wine>();
        await Assert.That(wine!.RunnerDirectory).IsEqualTo(tmp.Combine("runners/wine/wine-ge-8-26-x86_64"));
        await Assert.That(wine.Environment.ContainsKey("WINEDLLOVERRIDES")).IsTrue();
    }

    [Test]
    public async Task ResolvesProtonBuildViaUmu()
    {
        var tmp = TempDir();
        Touch(tmp.Combine("runners/wine/GE-Proton7-55/proton"));

        var runtime = LutrisConfigReader.ResolveRuntime(LutrisConfigReader.Parse(GeProton), tmp);

        var proton = await Assert.That(runtime).IsTypeOf<WineRuntime.ProtonViaUmu>();
        await Assert.That(proton!.ProtonDirectory).IsEqualTo(tmp.Combine("runners/wine/GE-Proton7-55"));
        await Assert.That(proton.Environment["MESA_LOADER_DRIVER_OVERRIDE"]).IsEqualTo("zink");
    }

    [Test]
    public async Task MissingVersionUsesGeProtonDefault()
    {
        var tmp = TempDir();
        Touch(tmp.Combine("runners/wine/ge-proton/proton"));
        var cfg = LutrisConfigReader.Parse(NoPrefixNoVersion) with { WineVersion = null };

        var runtime = LutrisConfigReader.ResolveRuntime(cfg, tmp);

        var proton = await Assert.That(runtime).IsTypeOf<WineRuntime.ProtonViaUmu>();
        await Assert.That(proton!.ProtonDirectory).IsEqualTo(tmp.Combine("runners/wine/ge-proton"));
    }

    [Test]
    public async Task MissingVersionWithoutGeProtonIsNull()
    {
        var tmp = TempDir();
        var warnings = new List<string>();
        var cfg = LutrisConfigReader.Parse(NoPrefixNoVersion) with { WineVersion = null };

        var runtime = LutrisConfigReader.ResolveRuntime(cfg, tmp, warnings.Add);

        await Assert.That(runtime).IsNull();
        await Assert.That(warnings.Count).IsEqualTo(1);
    }

    [Test]
    public async Task UnknownRunnerFolderIsNull()
    {
        var tmp = TempDir();
        var warnings = new List<string>();

        var runtime = LutrisConfigReader.ResolveRuntime(LutrisConfigReader.Parse(GeProton), tmp, warnings.Add);

        await Assert.That(runtime).IsNull();
        await Assert.That(warnings.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RunnerLevelVersionIsUsedWhenTheGameHasNone()
    {
        var tmp = TempDir();
        Touch(tmp.Combine("runners/wine/wine-staging-11.2-x86_64/bin/wine"));
        Touch(tmp.Combine("runners/wine/ge-proton/proton"));
        File.WriteAllText(tmp.Combine("runners/wine.yml").ToString(), "system:\n  env:\n    PROTON_VERB: waitforexitandrun\nwine:\n  version: wine-staging-11.2-x86_64\n");
        var game = LutrisConfigReader.Parse(NoPrefixNoVersion) with { WineVersion = null };

        var effective = LutrisConfigReader.Merge(LutrisConfigReader.ReadRunnerConfig(tmp), game);
        var runtime = LutrisConfigReader.ResolveRuntime(effective, tmp);

        var wine = await Assert.That(runtime).IsTypeOf<WineRuntime.Wine>();
        await Assert.That(wine!.RunnerDirectory).IsEqualTo(tmp.Combine("runners/wine/wine-staging-11.2-x86_64"));
        await Assert.That(wine.Environment["PROTON_VERB"]).IsEqualTo("waitforexitandrun");
        await Assert.That(wine.Environment["UMU_RUNTIME_UPDATE"]).IsEqualTo("0");
    }

    [Test]
    public async Task GameLevelWinsOverRunnerLevel()
    {
        var runner = LutrisConfigReader.Parse("system:\n  env:\n    A: runner\n    B: runner\nwine:\n  version: wine-staging-11.2-x86_64\n");
        var game = LutrisConfigReader.Parse("system:\n  env:\n    A: game\nwine:\n  version: GE-Proton7-55\n");

        var effective = LutrisConfigReader.Merge(runner, game);

        await Assert.That(effective.WineVersion).IsEqualTo("GE-Proton7-55");
        await Assert.That(effective.Env["A"]).IsEqualTo("game");
        await Assert.That(effective.Env["B"]).IsEqualTo("runner");
    }

    [Test]
    public async Task RunnerLevelGeProtonCountsAsUnset()
    {
        // Lutris: "Treat 'ge-proton' as if no version is set" at the runner level too
        var runner = LutrisConfigReader.Parse("wine:\n  version: ge-proton\n");
        var game = LutrisConfigReader.Parse("game:\n  exe: x\n");
        await Assert.That(LutrisConfigReader.Merge(runner, game).WineVersion).IsNull();
    }

    [Test]
    public async Task ProtonInTheRunnersProtonFolderResolves()
    {
        var tmp = TempDir();
        Touch(tmp.Combine("runners/proton/GE-Proton7-55/proton"));

        var runtime = LutrisConfigReader.ResolveRuntime(LutrisConfigReader.Parse(GeProton), tmp);

        var proton = await Assert.That(runtime).IsTypeOf<WineRuntime.ProtonViaUmu>();
        await Assert.That(proton!.ProtonDirectory).IsEqualTo(tmp.Combine("runners/proton/GE-Proton7-55"));
    }

    [Test]
    public async Task ProtonInSteamCompatibilityToolsResolves()
    {
        var tmp = TempDir();
        var lutris = tmp.Combine("lutris");
        Touch(tmp.Combine("Steam/compatibilitytools.d/GE-Proton7-55/proton"));

        var runtime = LutrisConfigReader.ResolveRuntime(LutrisConfigReader.Parse(GeProton), lutris);

        var proton = await Assert.That(runtime).IsTypeOf<WineRuntime.ProtonViaUmu>();
        await Assert.That(proton!.ProtonDirectory).IsEqualTo(tmp.Combine("Steam/compatibilitytools.d/GE-Proton7-55"));
    }

    [Test]
    [NotInParallel]
    public async Task SystemVersionUsesWineFromPath()
    {
        var tmp = TempDir();
        Touch(tmp.Combine("usr/bin/wine"));
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", tmp.Combine("usr/bin").ToString());
        try
        {
            var runtime = LutrisConfigReader.ResolveRuntime(LutrisConfigReader.Parse("wine:\n  version: system\n"), tmp.Combine("lutris"));
            var wine = await Assert.That(runtime).IsTypeOf<WineRuntime.Wine>();
            await Assert.That(wine!.RunnerDirectory).IsEqualTo(tmp.Combine("usr"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
    }

    [Test]
    public async Task FindByPrefixExpandsTilde()
    {
        var tmp = TempDir();
        var games = tmp.Combine("games");
        games.CreateDirectory();
        File.WriteAllText(games.Combine("a.yml").ToString(), "game:\n  prefix: ~/Juegos/pfx-cp\n");
        var home = FileSystem.Shared.GetKnownPath(KnownPath.HomeDirectory);

        await Assert.That(LutrisConfigReader.FindByPrefix(tmp, home.Combine("Juegos/pfx-cp"))).IsNotNull();
    }

    [Test]
    public async Task FindByPrefixIsDeterministicAndWarnsOnDuplicates()
    {
        var tmp = TempDir();
        var games = tmp.Combine("games");
        games.CreateDirectory();
        File.WriteAllText(games.Combine("b-2.yml").ToString(), "game:\n  prefix: /p\nwine:\n  version: second\n");
        File.WriteAllText(games.Combine("a-1.yml").ToString(), "game:\n  prefix: /p\nwine:\n  version: first\n");
        var warnings = new List<string>();

        var found = LutrisConfigReader.FindByPrefix(tmp, FileSystem.Shared.FromUnsanitizedFullPath("/p"), warnings.Add);

        await Assert.That(found!.WineVersion).IsEqualTo("first");
        await Assert.That(warnings.Count).IsEqualTo(1);
    }

    [Test]
    public async Task FindByPrefixWarnsAboutAnUnreadableYaml()
    {
        var tmp = TempDir();
        var games = tmp.Combine("games");
        games.CreateDirectory();
        File.WriteAllText(games.Combine("broken.yml").ToString(), "game: [unclosed\n");
        var warnings = new List<string>();

        await Assert.That(LutrisConfigReader.FindByPrefix(tmp, FileSystem.Shared.FromUnsanitizedFullPath("/p"), warnings.Add)).IsNull();
        await Assert.That(warnings.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RunnerFolderWithoutWineOrProtonIsNull()
    {
        var tmp = TempDir();
        tmp.Combine("runners/wine/GE-Proton7-55").CreateDirectory();
        var warnings = new List<string>();

        var runtime = LutrisConfigReader.ResolveRuntime(LutrisConfigReader.Parse(GeProton), tmp, warnings.Add);

        await Assert.That(runtime).IsNull();
        await Assert.That(warnings.Count).IsEqualTo(1);
    }
}
