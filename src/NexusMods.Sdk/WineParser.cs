using System.Collections.Immutable;
using System.Text;
using JetBrains.Annotations;
using NexusMods.Paths;

namespace NexusMods.Sdk;

/// <summary>
/// Parser for WINE.
/// </summary>
[PublicAPI]
public static class WineParser
{
    /// <summary>
    /// Environment name.
    /// </summary>
    public const string WineDllOverridesEnvironmentVariableName = "WINEDLLOVERRIDES";

    public static readonly RelativePath WinetricksLogFile = "winetricks.log";

    public static AbsolutePath GetWinetricksLogFilePath(AbsolutePath winePrefixDirectoryPath) => winePrefixDirectoryPath.Combine(WinetricksLogFile);

    /// <summary>
    /// Parses the given `winetricks.log` file and returns all installed packages.
    /// </summary>
    public static ImmutableHashSet<string> ParseWinetricksLogFile(AbsolutePath filePath)
    {
        if (!filePath.FileExists) return ImmutableHashSet<string>.Empty;

        using var stream = filePath.Read();
        return ParseWinetricksLogFile(stream);
    }

    /// <summary>
    /// Parses the given `winetricks.log` file and returns all installed packages.
    /// </summary>
    public static ImmutableHashSet<string> ParseWinetricksLogFile(Stream stream)
    {
        using var sr = new StreamReader(stream, Encoding.UTF8);

        var result = new HashSet<string>();

        while (sr.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            result.Add(line);
        }

        return result.ToImmutableHashSet();
    }

    /// <summary>
    /// Parses the environment variable out of a string.
    /// </summary>
    public static ImmutableArray<WineDllOverride> ParseEnvironmentVariable(ReadOnlySpan<char> environmentVariableValue)
    {
        if (environmentVariableValue.Length == 0) return [];

        var results = new List<WineDllOverride>();

        // https://gitlab.winehq.org/wine/wine/-/wikis/Wine-User's-Guide#winedlloverrides-dll-overrides

        // NOTE(erri120): DLLs are separated with a semicolon
        var splitEnumerator = environmentVariableValue.Split(';');
        foreach (var splitRange in splitEnumerator)
        {
            var section = environmentVariableValue[splitRange];

            var index = section.LastIndexOf('=');
            if (index == -1) continue;

            var namesSpan = section[..index];

            var dllNamesEnumerator = namesSpan.Split(',');
            var dllOverrideTypes = GetOverrideTypes(section);

            foreach (var range in dllNamesEnumerator)
            {
                var name = FixDllName(namesSpan[range]).ToString();
                results.Add(new WineDllOverride(name, dllOverrideTypes));
            }
        }

        return [..results];
    }

    private static ReadOnlySpan<char> FixDllName(ReadOnlySpan<char> input)
    {
        var index = input.LastIndexOf(".dll", StringComparison.OrdinalIgnoreCase);
        return index == -1 ? input : input[..index];
    }

    private static ImmutableArray<WineDllOverrideType> GetOverrideTypes(ReadOnlySpan<char> section)
    {
        var index = section.LastIndexOf('=');
        if (index == section.Length - 1) return WineDllOverride.Disabled;

        var typesSpan = section[(index + 1)..];

        var numTypes = typesSpan.Count(',') + 1;
        Span<WineDllOverrideType> overrideTypesSpan = stackalloc WineDllOverrideType[numTypes];

        var typesIndex = 0;
        var typesEnumerator = typesSpan.Split(',');
        foreach (var splitRange in typesEnumerator)
        {
            var typeSpan = typesSpan[splitRange];
            if (typeSpan.Length != 1) continue;

            var c = typeSpan[0];
            if (c is 'n') overrideTypesSpan[typesIndex++] = WineDllOverrideType.Native;
            if (c is 'b') overrideTypesSpan[typesIndex++] = WineDllOverrideType.BuiltIn;
        }

        var sliced = overrideTypesSpan[..typesIndex];
        return [..sliced];
    }

    /// <summary>
    /// Attempts to find and parse WINEDLLOVERRIDES from Lutris game configuration YAML files.
    /// Lutris stores DLL overrides under <c>wine.overrides</c> in its YAML, not in the wine registry,
    /// so <see cref="ParseDllOverridesFromRegistry"/> returns nothing for Lutris-managed games.
    /// </summary>
    /// <param name="winePrefixPath">The wine prefix path to match against (<c>game.prefix</c> in the Lutris YAML).</param>
    public static ImmutableArray<WineDllOverride> ParseDllOverridesFromLutrisConfigs(string winePrefixPath)
    {
        // Lutris stores game configs under XDG_DATA_HOME (not XDG_CONFIG_HOME)
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var lutrisGamesDir = Path.Combine(xdgData, "lutris", "games");

        if (!Directory.Exists(lutrisGamesDir)) return [];

        foreach (var ymlFile in Directory.EnumerateFiles(lutrisGamesDir, "*.yml", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var content = File.ReadAllText(ymlFile);

                // The wine prefix is stored as 'prefix: /path' under the 'game:' section.
                // A simple string-contains is enough to identify the matching config.
                if (!content.Contains(winePrefixPath, StringComparison.OrdinalIgnoreCase)) continue;

                var result = ParseDllOverridesFromLutrisYaml(content);
                if (!result.IsEmpty) return result;
            }
            catch
            {
                // ignore parse errors for individual config files
            }
        }

        return [];
    }

    /// <summary>
    /// Parses DLL overrides from a Lutris game YAML config string.
    /// Handles two locations:
    /// <list type="number">
    ///   <item><c>wine.overrides</c> — set via Runner options → DLL overrides UI (e.g. <c>winmm: n,b</c>)</item>
    ///   <item><c>system.env.WINEDLLOVERRIDES</c> — set via System options → Environment variables</item>
    /// </list>
    /// </summary>
    public static ImmutableArray<WineDllOverride> ParseDllOverridesFromLutrisYaml(string yamlContent)
    {
        // --- Method 1: wine.overrides section (Lutris DLL overrides UI) ---
        // Expected YAML shape:
        //   wine:
        //     overrides:
        //       winmm: n,b
        //       version: n,b
        var fromWineOverrides = ParseLutrisWineOverridesSection(yamlContent);
        if (!fromWineOverrides.IsEmpty) return fromWineOverrides;

        // --- Method 2: WINEDLLOVERRIDES env var in system.env section ---
        // Expected YAML shape:
        //   system:
        //     env:
        //       WINEDLLOVERRIDES: "winmm=n,b;version=n,b"
        foreach (var lineRange in yamlContent.AsSpan().Split('\n'))
        {
            var trimmed = yamlContent.AsSpan()[lineRange].Trim();
            if (!trimmed.StartsWith("WINEDLLOVERRIDES:", StringComparison.OrdinalIgnoreCase)) continue;

            var value = trimmed["WINEDLLOVERRIDES:".Length..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') value = value[1..^1];

            var result = ParseEnvironmentVariable(value);
            if (!result.IsEmpty) return result;
        }

        return [];
    }

    // Parses the 'wine.overrides' YAML section.
    // Each entry is '    dllname: n,b' (2-space indent relative to 'overrides:').
    private static ImmutableArray<WineDllOverride> ParseLutrisWineOverridesSection(string yamlContent)
    {
        // Find 'wine:' section, then 'overrides:' within it
        var wineIdx = yamlContent.IndexOf("\nwine:", StringComparison.OrdinalIgnoreCase);
        if (wineIdx == -1) return [];

        var overridesIdx = yamlContent.IndexOf("overrides:", wineIdx, StringComparison.OrdinalIgnoreCase);
        if (overridesIdx == -1) return [];

        var results = new List<WineDllOverride>();

        foreach (var lineRange in yamlContent.AsSpan(overridesIdx).Split('\n'))
        {
            var line = yamlContent.AsSpan(overridesIdx)[lineRange];
            if (line.IsEmpty) continue;

            // Stop when we hit a line that is not deeper-indented (new top-level key)
            var trimmed = line.TrimStart();
            if (trimmed.IsEmpty) continue;
            var indent = line.Length - trimmed.Length;
            if (indent == 0 && results.Count > 0) break; // back to root level

            // Skip the 'overrides:' header line itself
            if (trimmed.StartsWith("overrides:", StringComparison.OrdinalIgnoreCase)) continue;

            // Each override entry: '    dllname: types'  where types = 'n,b' or 'native,builtin'
            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx == -1) continue;

            var dllName = trimmed[..colonIdx].Trim().ToString();
            var typesSpan = trimmed[(colonIdx + 1)..].Trim();

            // Reuse ParseEnvironmentVariable by constructing 'dllname=types'
            var fakeEnvEntry = string.Concat(dllName, "=", typesSpan);

            var parsed = ParseEnvironmentVariable(fakeEnvEntry);
            results.AddRange(parsed);
        }

        return [..results];
    }

/// <summary>
    /// Gets the DLL overrides section.
    /// </summary>
    public static ReadOnlySpan<char> GetWineDllOverridesSection(ReadOnlySpan<char> input)
    {
        const string prefix = "WINEDLLOVERRIDES=";

        var index = input.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index == -1) return ReadOnlySpan<char>.Empty;

        var span = input[(index + prefix.Length)..];

        var whitespaceIndex = span.IndexOf(' ');
        if (whitespaceIndex != -1)
            span = span[..whitespaceIndex];

        if (span.StartsWith('"'))
            span = span[1..];
        if (span.EndsWith('"'))
            span = span[..^1];

        return span;
    }

    /// <summary>
    /// Parses the DLL overrides out of a registry string (e.g. `user.reg`).
    /// </summary>
    public static ImmutableArray<WineDllOverride> ParseDllOverridesFromRegistry(string content)
    {
        const string sectionHeader = "[Software\\Wine\\DllOverrides]";
        var sectionStart = content.IndexOf(sectionHeader, StringComparison.OrdinalIgnoreCase);
        if (sectionStart == -1) return [];

        var results = new List<WineDllOverride>();
        var lines = content.AsSpan(sectionStart).Split('\n');
        // Skip the header
        lines.MoveNext();

        foreach (var lineRange in lines)
        {
            var line = content.AsSpan(sectionStart)[lineRange].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("[")) break; // Start of next section

            var eqIndex = line.IndexOf('=');
            if (eqIndex == -1) continue;

            var key = line[..eqIndex].Trim('"').ToString();
            var value = line[(eqIndex + 1)..].Trim('"');

            var dllOverrideTypes = GetOverrideTypes(value);
            results.Add(new WineDllOverride(key, dllOverrideTypes));
        }

        return [..results];
    }
}
