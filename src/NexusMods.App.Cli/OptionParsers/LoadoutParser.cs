using System.Globalization;
using JetBrains.Annotations;
using NexusMods.Abstractions.Games;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.CLI.OptionParsers;

/// <summary>
/// Parses a string into a loadout marker
/// </summary>
[UsedImplicitly]
internal class LoadoutParser(IConnection conn, IOptionParser<IGame> gameParser) : IOptionParser<Loadout.ReadOnly>
{
    public bool TryParse(string input, out Loadout.ReadOnly value, out string error)
    {
        var db = conn.Db;
        error = string.Empty;
        if (EntityId.TryParseFromHex(input, out var parsedId))
        {
            var loadout = Loadout.Load(db, parsedId);
            if (loadout.IsValid())
            {
                value = loadout;
                return true;
            }
        }
        
        // An id in the format of: LoadoutId:00000000000000000000000000
        if (input.StartsWith("LoadoutId:") && input.Length == 25 && ulong.TryParse(input[10..], NumberStyles.HexNumber, null, out var rawId))
        {
            var loadout = Loadout.Load(db, EntityId.From(rawId));
            if (loadout.IsValid())
            {
                value = loadout;
                return true;
            }
        }
        
        // In the format of "<Game>/<ShortName>"
        if (input.Contains("/"))
        {
            var parts = input.Split('/');
            var game = parts[0];
            var shortName = parts[1];

            if (gameParser.TryParse(game, out var gameValue, out _))
            {
                if (Loadout
                    .FindByShortName(db, shortName)
                    .TryGetFirst(l => l.Installation.GameId == gameValue.GameId, out var foundLoadout))
                {
                    value = foundLoadout;
                    return true;
                }
            }
        }

        var found = Loadout.FindByName(db, input).ToArray();

        if (found.Length == 1)
        {
            value = found[0];
            return true;
        }

        value = default;
        error = found.Length == 0
            ? $"No loadout found with name or id {input}"
            : $"Multiple loadouts found with name {input}";
        return false;
    }
}
