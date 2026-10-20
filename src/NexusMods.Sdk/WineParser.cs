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
    /// DLL overrides of the Lutris game whose <c>game.prefix</c> is <paramref name="winePrefixPath"/>. Lutris keeps them
    /// in its YAML (<c>wine.overrides</c> / <c>system.env.WINEDLLOVERRIDES</c>), not in the prefix registry.
    /// </summary>
    public static ImmutableArray<WineDllOverride> ParseDllOverridesFromLutrisConfigs(string winePrefixPath)
    {
        var fs = FileSystem.Shared;
        return LutrisConfigReader.FindByPrefix(LutrisConfigReader.DefaultDataDirectory(fs), fs.FromUnsanitizedFullPath(winePrefixPath))?.Overrides ?? [];
    }

    /// <summary>DLL overrides declared in a Lutris game YAML (see <see cref="LutrisConfigReader.Parse"/>).</summary>
    public static ImmutableArray<WineDllOverride> ParseDllOverridesFromLutrisYaml(string yamlContent) => LutrisConfigReader.Parse(yamlContent).Overrides;

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
