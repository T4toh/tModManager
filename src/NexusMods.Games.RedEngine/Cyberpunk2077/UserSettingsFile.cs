using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.RedEngine.Cyberpunk2077;

/// <summary>
/// CP2077's UserSettings.json: { "version": 140, "data": [ { "group_name", "options": [ { "name", "type",
/// "value", "default_value", ... } ] } ] }. A key is "group_name/name" (group names start with "/", so the
/// split is on the LAST slash) and a value is a JSON literal written into "value"; everything else in the
/// option, the group and the document is kept as it was.
/// </summary>
public class UserSettingsFile(GamePath path) : ASettingsIntrinsicFile<JsonNode>(path)
{
    private const int DefaultVersion = 140;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    protected override JsonNode Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject { ["version"] = DefaultVersion, ["data"] = new JsonArray() };
        var node = JsonNode.Parse(text) ?? throw new InvalidOperationException("UserSettings.json vacío");
        if (node["data"] is not JsonArray) throw new InvalidOperationException("UserSettings.json sin 'data'");
        return node;
    }

    protected override string Serialize(JsonNode document) => document.ToJsonString(Indented);

    protected override bool TryGet(JsonNode document, string key, out string value)
    {
        value = string.Empty;
        var (group, name) = Split(key);
        var option = OptionsOf(FindGroup(document, group))?.FirstOrDefault(o => NameOf(o) == name);
        if (option is null) return false;
        value = option["value"]?.ToJsonString() ?? "null";
        return true;
    }

    /// <summary>JSON numbers compare by value ("5.0" is "5"); strings, bools and the rest compare as printed.</summary>
    protected override string NormalizeLiteral(string literal) =>
        double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("R", CultureInfo.InvariantCulture)
            : literal;

    protected override void Set(JsonNode document, string key, string value)
    {
        var (group, name) = Split(key);
        JsonNode? literal;
        try { literal = JsonNode.Parse(value); }
        catch (JsonException e) { throw new InvalidOperationException($"El valor de '{key}' no es un literal JSON válido: {value}", e); }

        var groupNode = FindGroup(document, group);
        if (groupNode is null)
        {
            groupNode = new JsonObject { ["group_name"] = group, ["options"] = new JsonArray() };
            document["data"]!.AsArray().Add(groupNode);
        }
        var options = OptionsOf(groupNode);
        if (options is null)
        {
            // Missing or not an array (a broken file): the group gets a fresh list
            options = new JsonArray();
            groupNode["options"] = options;
        }
        var option = options.FirstOrDefault(o => NameOf(o) == name);
        if (option is null)
        {
            // The game validates by name; no invented "type" or defaults.
            options.Add(new JsonObject { ["name"] = name, ["value"] = literal });
            return;
        }
        option["value"] = literal;
    }

    private static (string Group, string Name) Split(string key)
    {
        var slash = key.LastIndexOf('/');
        if (slash <= 0 || slash == key.Length - 1)
            throw new InvalidOperationException($"La clave '{key}' tiene que ser 'grupo/opción' (ej. /graphics/advanced/DLSS)");
        return (key[..slash], key[(slash + 1)..]);
    }

    // The game writes strings here; a file edited by hand may not. Anything else is simply "not that group/option",
    // never an exception in the middle of an apply.
    private static JsonNode? FindGroup(JsonNode document, string group) =>
        (document["data"] as JsonArray)?.FirstOrDefault(g => StringOf(g?["group_name"]) == group);

    private static JsonArray? OptionsOf(JsonNode? group) => group?["options"] as JsonArray;

    private static string? NameOf(JsonNode? option) => StringOf(option?["name"]);

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
