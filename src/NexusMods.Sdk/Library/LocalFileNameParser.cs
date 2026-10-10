using System.Text.RegularExpressions;
using JetBrains.Annotations;

namespace NexusMods.Sdk.Library;

/// <summary>
/// Guesses display name and version from a hand-picked file name. Only used to prefill a dialog;
/// the user can always correct it.
/// </summary>
[PublicAPI]
public static partial class LocalFileNameParser
{
    // A version is "v" or a separator, then digits with at least one dot ("1.2", "1.2.3") or a bare
    // number right after "v" ("V3"). Nexus names ("Mod-123-1-0-1757") use dashes, not dots, so they
    // are left alone on purpose: "123" there is the mod id, not a version.
    [GeneratedRegex(@"(?:^|[\s_\-(])[vV]?(\d+(?:\.\d+)+)(?=$|[\s_\-).\]])|(?:^|[\s_\-(])[vV](\d+)(?=$|[\s_\-).\]])", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    public static string? TryParseVersion(string fileName)
    {
        var stem = DisplayName(fileName);
        if (stem.Length == 0) return null;
        var match = VersionRegex().Match(stem);
        if (!match.Success) return null;
        var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        // A stem that is only the number ("2077") is a name, not a version.
        return value == stem ? null : value;
    }

    public static string DisplayName(string fileName) => Path.GetFileNameWithoutExtension(fileName);
}
