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

    /// <summary>The game config whose <c>game.prefix</c> is <paramref name="winePrefix"/> (trailing separator and case ignored), or null.</summary>
    public static LutrisGameConfig? FindByPrefix(AbsolutePath lutrisDataDir, AbsolutePath winePrefix)
    {
        var gamesDir = lutrisDataDir.Combine("games");
        if (!gamesDir.DirectoryExists()) return null;
        var wanted = Normalize(winePrefix.ToString());

        foreach (var yml in gamesDir.EnumerateFiles("*.yml", recursive: false))
        {
            LutrisGameConfig config;
            try { config = Parse(File.ReadAllText(yml.ToString())); }
            catch (Exception) { continue; }
            if (config.Prefix is { } prefix && string.Equals(Normalize(prefix), wanted, StringComparison.OrdinalIgnoreCase))
                return config;
        }
        return null;
    }

    /// <summary>
    /// The runtime for <paramref name="config"/>: a Wine build (<c>bin/wine</c>) or a Proton build (<c>proton</c>) under
    /// <c>runners/wine/&lt;version&gt;</c>. Null, with a warning, when the runner folder is missing or has neither.
    /// </summary>
    public static WineRuntime? ResolveRuntime(LutrisGameConfig config, AbsolutePath lutrisDataDir, Action<string>? warn = null)
    {
        var version = config.WineVersion ?? DefaultRunner;
        var runnerDir = lutrisDataDir.Combine("runners/wine").Combine(version);
        if (runnerDir.Combine("bin/wine").FileExists)
            return new WineRuntime.Wine(runnerDir, BuildEnvironment(config));
        if (runnerDir.Combine("proton").FileExists)
            return new WineRuntime.ProtonViaUmu(runnerDir, BuildEnvironment(config));

        warn?.Invoke(runnerDir.DirectoryExists()
            ? $"El runner de Lutris {runnerDir} no tiene bin/wine ni proton; la app no sabe cómo usarlo"
            : $"No existe el runner de Lutris {runnerDir} (wine.version = {config.WineVersion ?? "sin definir, default " + DefaultRunner})");
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
