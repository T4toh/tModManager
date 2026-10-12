
using NexusMods.Sdk.Games;

namespace NexusMods.Games.RedEngine;

internal class Constants
{
    internal static readonly GamePath RedModPath = new(LocationId.Game, "tools/redmod/bin/redMod.exe");
    internal static readonly GamePath RedModInstallFolder = new(LocationId.Game, "mods");
    internal static readonly GamePath RedModDeployFolder = new(LocationId.Game, "r6/cache/modded");
    /// <summary>Where the apply writes the REDmod load order; the deploy reads it as C:\modlist.txt.</summary>
    internal static readonly GamePath RedModListPath = new(LocationId.WinePrefix, "drive_c/modlist.txt");
}
