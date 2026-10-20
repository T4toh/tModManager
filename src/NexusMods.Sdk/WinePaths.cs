using JetBrains.Annotations;
using NexusMods.Paths;

namespace NexusMods.Sdk;

/// <summary>
/// Paths as a Windows program inside a Wine prefix sees them.
/// </summary>
[PublicAPI]
public static class WinePaths
{
    /// <summary>
    /// <c>C:\...</c> for a file inside the prefix's <c>drive_c</c>, <c>Z:\...</c> (the Linux root) for anything else.
    /// </summary>
    public static string ToWindowsPath(AbsolutePath path, AbsolutePath winePrefix)
    {
        var driveC = winePrefix.Combine("drive_c");
        if (path.InFolder(driveC))
            return @"C:\" + path.RelativeTo(driveC).ToString().Replace('/', '\\');
        return "Z:" + path.ToString().Replace('/', '\\');
    }
}
