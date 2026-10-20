using System.Collections.Immutable;
using JetBrains.Annotations;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using YamlDotNet.Serialization;

namespace NexusMods.Sdk;

/// <summary>
/// A Lutris game config (<c>$XDG_DATA_HOME/lutris/games/&lt;slug&gt;-&lt;id&gt;.yml</c>), the parts the app needs
/// to run executables in that game's prefix.
/// </summary>
/// <param name="Prefix"><c>game.prefix</c>, as written.</param>
/// <param name="Exe"><c>game.exe</c>.</param>
/// <param name="WineVersion"><c>wine.version</c>: the runner folder name under <c>runners/wine/</c>; null means Lutris's default.</param>
/// <param name="Env"><c>system.env</c> without <c>WINEDLLOVERRIDES</c> (that one lives in <paramref name="Overrides"/>).</param>
/// <param name="Overrides"><c>wine.overrides</c> ∪ <c>system.env.WINEDLLOVERRIDES</c>, <c>wine.overrides</c> wins per DLL.</param>
[PublicAPI]
public sealed record LutrisGameConfig(
    string? Prefix,
    string? Exe,
    string? WineVersion,
    ImmutableDictionary<string, string> Env,
    ImmutableArray<WineDllOverride> Overrides);

/// <summary>
/// Reads Lutris game configs and resolves the Wine runtime they point at. Lutris is the only non-Steam
/// launcher supported today; Heroic/Bottles would be another reader producing the same <see cref="WineRuntime"/>.
/// </summary>
[PublicAPI]
public static class LutrisConfigReader
{
    /// <summary>Lutris's default runner when a game config has no <c>wine.version</c> (a Proton build, launched via umu).</summary>
    public const string DefaultRunner = "ge-proton";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();

    /// <summary><c>$XDG_DATA_HOME/lutris</c> (default <c>~/.local/share/lutris</c>).</summary>
    public static AbsolutePath DefaultDataDirectory(IFileSystem fs)
    {
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = string.IsNullOrWhiteSpace(xdgData)
            ? fs.GetKnownPath(KnownPath.HomeDirectory).Combine(".local/share")
            : fs.FromUnsanitizedFullPath(xdgData);
        return baseDir.Combine("lutris");
    }

    /// <summary>Parses one game config. Unknown keys (including Lutris's stray <c>null: {}</c>) are ignored.</summary>
    public static LutrisGameConfig Parse(string yaml)
    {
        var dto = Deserializer.Deserialize<ConfigDto>(yaml) ?? new ConfigDto();
        var env = dto.System?.Env ?? new Dictionary<string, string>();

        var overrides = new List<WineDllOverride>();
        if (env.TryGetValue(WineParser.WineDllOverridesEnvironmentVariableName, out var fromEnv))
            overrides.AddRange(WineParser.ParseEnvironmentVariable(fromEnv));
        foreach (var (dll, types) in dto.Wine?.Overrides ?? new Dictionary<string, string>())
        {
            var parsed = WineParser.ParseEnvironmentVariable($"{dll}={types}");
            if (parsed.IsEmpty) continue;
            overrides.RemoveAll(o => string.Equals(o.DllName, dll, StringComparison.OrdinalIgnoreCase));
            overrides.AddRange(parsed);
        }

        return new LutrisGameConfig(
            Prefix: NullIfEmpty(dto.Game?.Prefix),
            Exe: NullIfEmpty(dto.Game?.Exe),
            WineVersion: NullIfEmpty(dto.Wine?.Version),
            Env: env.Where(kv => !string.Equals(kv.Key, WineParser.WineDllOverridesEnvironmentVariableName, StringComparison.OrdinalIgnoreCase))
                .ToImmutableDictionary(kv => kv.Key, kv => kv.Value),
            Overrides: [..overrides]);
    }

    /// <summary>
    /// The game config whose <c>game.prefix</c> is <paramref name="winePrefix"/> (<c>~</c> expanded, trailing separator and
    /// case ignored), or null. Files are read in name order; a second match or an unreadable file is reported through
    /// <paramref name="warn"/>.
    /// </summary>
    public static LutrisGameConfig? FindByPrefix(AbsolutePath lutrisDataDir, AbsolutePath winePrefix, Action<string>? warn = null)
    {
        var gamesDir = lutrisDataDir.Combine("games");
        if (!gamesDir.DirectoryExists()) return null;
        var wanted = Normalize(winePrefix.ToString());
        var home = FileSystem.Shared.GetKnownPath(KnownPath.HomeDirectory).ToString();

        LutrisGameConfig? found = null;
        foreach (var yml in gamesDir.EnumerateFiles("*.yml", recursive: false).OrderBy(f => f.FileName, StringComparer.Ordinal))
        {
            LutrisGameConfig config;
            try { config = Parse(File.ReadAllText(yml.ToString())); }
            catch (Exception e) { warn?.Invoke($"No se pudo leer la configuración de Lutris {yml}: {e.Message}"); continue; }
            if (config.Prefix is not { } prefix) continue;
            if (prefix.StartsWith("~/", StringComparison.Ordinal)) prefix = home + prefix[1..];
            if (!string.Equals(Normalize(prefix), wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (found is not null)
            {
                warn?.Invoke($"Más de una entrada de Lutris usa el prefix {winePrefix}; se usa la primera por nombre de archivo ({yml.FileName} se ignora)");
                continue;
            }
            found = config;
        }
        return found;
    }

    /// <summary>The Wine runner's own config (<c>runners/wine.yml</c>: default version and env for every game), or null.</summary>
    public static LutrisGameConfig? ReadRunnerConfig(AbsolutePath lutrisDataDir)
    {
        var file = lutrisDataDir.Combine("runners/wine.yml");
        if (!file.FileExists) return null;
        try { return Parse(File.ReadAllText(file.ToString())); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// What Lutris runs with: the game's values over the runner's. A runner-level <c>ge-proton</c> counts as unset,
    /// as in Lutris (<c>read_version_from_config</c>). Prefix and exe always come from the game.
    /// </summary>
    public static LutrisGameConfig Merge(LutrisGameConfig? runner, LutrisGameConfig game)
    {
        if (runner is null) return game;
        var runnerVersion = string.Equals(runner.WineVersion, DefaultRunner, StringComparison.OrdinalIgnoreCase) ? null : runner.WineVersion;
        var overrides = runner.Overrides.ToList();
        foreach (var o in game.Overrides)
        {
            overrides.RemoveAll(r => string.Equals(r.DllName, o.DllName, StringComparison.OrdinalIgnoreCase));
            overrides.Add(o);
        }
        return new LutrisGameConfig(
            Prefix: game.Prefix,
            Exe: game.Exe,
            WineVersion: game.WineVersion ?? runnerVersion,
            Env: runner.Env.SetItems(game.Env),
            Overrides: [..overrides]);
    }

    /// <summary>
    /// The runtime for <paramref name="config"/>: <c>system</c> is the <c>wine</c> on PATH; otherwise a Wine build
    /// (<c>bin/wine</c>) or a Proton build (<c>proton</c>) named <c>wine.version</c> under <c>runners/wine/</c>,
    /// <c>runners/proton/</c> or Steam's <c>compatibilitytools.d/</c> (next to the lutris data dir). Null, with a
    /// warning, when nothing matches.
    /// </summary>
    public static WineRuntime? ResolveRuntime(LutrisGameConfig config, AbsolutePath lutrisDataDir, Action<string>? warn = null)
    {
        var version = config.WineVersion ?? DefaultRunner;
        if (string.Equals(version, "system", StringComparison.OrdinalIgnoreCase))
        {
            var systemWine = FindOnPath("wine");
            if (systemWine is { } wine) return new WineRuntime.Wine(wine.Parent.Parent, BuildEnvironment(config));
            warn?.Invoke("El juego usa el wine del sistema en Lutris, pero no hay wine en el PATH");
            return null;
        }

        AbsolutePath[] roots = [lutrisDataDir.Combine("runners/wine"), lutrisDataDir.Combine("runners/proton"), lutrisDataDir.Parent.Combine("Steam/compatibilitytools.d")];
        foreach (var runnerDir in roots.Select(r => r.Combine(version)))
        {
            if (runnerDir.Combine("bin/wine").FileExists) return new WineRuntime.Wine(runnerDir, BuildEnvironment(config));
            if (runnerDir.Combine("proton").FileExists) return new WineRuntime.ProtonViaUmu(runnerDir, BuildEnvironment(config));
        }

        var existing = roots.Select(r => r.Combine(version)).FirstOrDefault(d => d.DirectoryExists());
        warn?.Invoke(existing != default
            ? $"El runner de Lutris {existing} no tiene bin/wine ni proton; la app no sabe cómo usarlo"
            : $"No se encontró el runner de Lutris \"{version}\" (wine.version {(config.WineVersion is null ? "sin definir, default " + DefaultRunner : "del juego o del runner")}) en {string.Join(", ", roots)}");
        return null;
    }

    /// <summary>First <paramref name="name"/> found in the directories of <c>PATH</c>, or null.</summary>
    public static AbsolutePath? FindOnPath(string name)
    {
        var fs = FileSystem.Shared;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            AbsolutePath candidate;
            try { candidate = fs.FromUnsanitizedFullPath(dir).Combine(name); }
            catch (Exception) { continue; }
            if (candidate.FileExists) return candidate;
        }
        return null;
    }

    /// <summary><see cref="LutrisGameConfig.Env"/> plus <c>WINEDLLOVERRIDES</c> built from <see cref="LutrisGameConfig.Overrides"/> when there are any.</summary>
    public static ImmutableDictionary<string, string> BuildEnvironment(LutrisGameConfig config)
    {
        if (config.Overrides.IsDefaultOrEmpty) return config.Env;
        var value = string.Join(';', config.Overrides.Select(o => o.ToString()));
        return config.Env.SetItem(WineParser.WineDllOverridesEnvironmentVariableName, value);
    }

    private static string Normalize(string path) => path.TrimEnd('/');
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // YamlDotNet maps lowercase keys to these by name (default naming convention: exact match).
    private sealed class ConfigDto
    {
        [YamlMember(Alias = "game")] public GameDto? Game { get; set; }
        [YamlMember(Alias = "system")] public SystemDto? System { get; set; }
        [YamlMember(Alias = "wine")] public WineDto? Wine { get; set; }
    }

    private sealed class GameDto
    {
        [YamlMember(Alias = "exe")] public string? Exe { get; set; }
        [YamlMember(Alias = "prefix")] public string? Prefix { get; set; }
    }

    private sealed class SystemDto
    {
        [YamlMember(Alias = "env")] public Dictionary<string, string>? Env { get; set; }
    }

    private sealed class WineDto
    {
        [YamlMember(Alias = "version")] public string? Version { get; set; }
        [YamlMember(Alias = "overrides")] public Dictionary<string, string>? Overrides { get; set; }
    }
}
