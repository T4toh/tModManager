using System.Text.Json.Nodes;
using FluentAssertions;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Sdk.Games;
using Xunit;

namespace NexusMods.Games.RedEngine.Tests;

public class UserSettingsFileTests
{
    private const string Sample = """
        {
          "version": 140,
          "data": [
            { "group_name": "/controls/fpp_camera", "options": [
              { "name": "FPP_MouseX", "type": "float", "value": 5.0, "default_value": 5.0, "min_value": 1.0, "max_value": 30.0, "step_value": 1.0 },
              { "name": "FPP_MouseInvertY", "type": "bool", "value": false, "default_value": false }
            ] },
            { "group_name": "/graphics/advanced", "options": [
              { "name": "DLSS", "type": "name_list", "value": "Auto", "default_value": "Auto", "extra": [1, 2] }
            ] }
          ],
          "unknown_top_level": { "keep": true }
        }
        """;

    private sealed class Exposed() : UserSettingsFile(new GamePath(LocationId.WinePrefix, "x/UserSettings.json"))
    {
        public JsonNode P(string text) => Parse(text);
        public string S(JsonNode doc) => Serialize(doc);
        public bool G(JsonNode doc, string key, out string value) => TryGet(doc, key, out value);
        public void Put(JsonNode doc, string key, string value) => Set(doc, key, value);
        public string N(string literal) => NormalizeLiteral(literal);
    }

    private readonly Exposed _file = new();

    [Fact]
    public void TryGet_ExistingKey_ReturnsTheJsonLiteral()
    {
        var doc = _file.P(Sample);
        _file.G(doc, "/controls/fpp_camera/FPP_MouseX", out var v).Should().BeTrue();
        v.Should().Be("5.0", "the literal as the file has it");
        _file.G(doc, "/graphics/advanced/DLSS", out v).Should().BeTrue();
        v.Should().Be("\"Auto\"");
        _file.G(doc, "/controls/fpp_camera/FPP_MouseInvertY", out v).Should().BeTrue();
        v.Should().Be("false");
    }

    [Fact]
    public void TryGet_MissingKey_IsFalse()
    {
        var doc = _file.P(Sample);
        _file.G(doc, "/graphics/advanced/Nope", out _).Should().BeFalse();
        _file.G(doc, "/nope/DLSS", out _).Should().BeFalse();
    }

    [Fact]
    public void Set_ExistingKey_ReplacesOnlyValue_AndKeepsEverythingElse()
    {
        var doc = _file.P(Sample);
        _file.Put(doc, "/graphics/advanced/DLSS", "\"Off\"");
        var roundTrip = JsonNode.Parse(_file.S(doc))!;

        roundTrip["version"]!.GetValue<int>().Should().Be(140);
        roundTrip["unknown_top_level"]!["keep"]!.GetValue<bool>().Should().BeTrue();
        var dlss = roundTrip["data"]![1]!["options"]![0]!;
        dlss["value"]!.GetValue<string>().Should().Be("Off");
        dlss["type"]!.GetValue<string>().Should().Be("name_list");
        dlss["default_value"]!.GetValue<string>().Should().Be("Auto");
        dlss["extra"]!.AsArray().Count.Should().Be(2);
        roundTrip["data"]![0]!["options"]![0]!["value"]!.GetValue<double>().Should().Be(5.0);
    }

    [Fact]
    public void Set_UnknownOption_AddsItToTheGroup_WithoutInventingAType()
    {
        var doc = _file.P(Sample);
        _file.Put(doc, "/graphics/advanced/NewThing", "true");
        var group = JsonNode.Parse(_file.S(doc))!["data"]![1]!;
        var added = group["options"]!.AsArray().Single(o => o!["name"]!.GetValue<string>() == "NewThing")!;
        added["value"]!.GetValue<bool>().Should().BeTrue();
        added["type"].Should().BeNull();
    }

    [Fact]
    public void Set_UnknownGroup_AddsIt()
    {
        var doc = _file.P(Sample);
        _file.Put(doc, "/mods/Foo", "1");
        var data = JsonNode.Parse(_file.S(doc))!["data"]!.AsArray();
        data.Count.Should().Be(3);
        data[2]!["group_name"]!.GetValue<string>().Should().Be("/mods");
        data[2]!["options"]![0]!["name"]!.GetValue<string>().Should().Be("Foo");
    }

    [Fact]
    public void Set_InvalidLiteral_Throws()
    {
        var doc = _file.P(Sample);
        var act = () => _file.Put(doc, "/graphics/advanced/DLSS", "Off");
        act.Should().Throw<InvalidOperationException>().WithMessage("*/graphics/advanced/DLSS*");
    }

    [Fact]
    public void Set_KeyWithoutSlash_Throws()
    {
        var doc = _file.P(Sample);
        var act = () => _file.Put(doc, "DLSS", "1");
        act.Should().Throw<InvalidOperationException>().WithMessage("*DLSS*");
    }

    [Fact]
    public void Parse_EmptyText_IsTheMinimalDocument()
    {
        var doc = _file.P(string.Empty);
        doc["version"]!.GetValue<int>().Should().Be(140);
        doc["data"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void Serialize_IsIndentedUtf8WithoutBom()
    {
        var text = _file.S(_file.P(Sample));
        text.Should().StartWith("{");
        text.Should().Contain("\n  \"version\": 140");
    }

    [Fact]
    public void HandEditedShapes_NeverThrow()
    {
        // A numeric "name", a numeric "group_name" and an "options" that is not an array: not ours, not a crash
        const string broken = """
            {"version":140,"data":[
              {"group_name":7,"options":[]},
              {"group_name":"/a","options":{"name":"x"}},
              {"group_name":"/b","options":[{"name":3,"value":1},{"name":"k","value":2}]},
              "junk", null
            ]}
            """;
        var doc = _file.P(broken);

        _file.G(doc, "/b/k", out var value).Should().BeTrue();
        value.Should().Be("2");
        _file.G(doc, "/a/x", out _).Should().BeFalse();
        _file.G(doc, "/b/3", out _).Should().BeFalse();

        _file.Put(doc, "/a/x", "true");
        _file.G(doc, "/a/x", out value).Should().BeTrue("a non-array options is replaced by a fresh list");
        value.Should().Be("true");
    }

    [Fact]
    public void NormalizeLiteral_MakesNumbersComparable_AndLeavesTheRestAlone()
    {
        _file.N("5.0").Should().Be(_file.N("5"));
        _file.N("7.50").Should().Be(_file.N("7.5"));
        _file.N("\"Off\"").Should().Be("\"Off\"");
        _file.N("true").Should().Be("true");
    }
}
