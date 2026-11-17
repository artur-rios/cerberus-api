using System.Text.Json.Nodes;

namespace ArturRios.Cerberus.WebApi.Tests;

// The document is OpenAPI 3.1, which says "can be null" the way JSON Schema does: a "null" in a scalar's or an
// array's type, and anyOf [{ $ref }, { type: "null" }] for a reference. 3.1 has no nullable keyword, and a client
// generator reading 3.1 (swagger_parser 2) ignores it, so none may be left.
public sealed class OpenApi31ContractTests
{
    private static readonly string[] s_compositions = ["allOf", "anyOf", "oneOf"];

    [UnitFact]
    public void GivenPublishedDocument_WhenVersionRead_ThenItIsOpenApi31()
    {
        Assert.StartsWith("3.1.", OpenApiBodies.Document()["openapi"]!.GetValue<string>());
    }

    [UnitFact]
    public void GivenPublishedDocument_WhenWalked_ThenNoNullableKeywordIsLeft()
    {
        var found = Keywords(OpenApiBodies.Document(), "#").Where(k => k.Name == "nullable").Select(k => k.Path).ToArray();
        Assert.True(found.Length == 0, "nullable is left at: " + string.Join(", ", found));
    }

    [UnitFact]
    public void GivenPublishedDocument_WhenComposedReferencePropertiesRead_ThenEachIsTheReferenceOrNull()
    {
        var document = OpenApiBodies.Document();
        var violations = new List<string>();
        var composed = 0;
        foreach (var (name, schema) in document["components"]!["schemas"]!.AsObject())
        {
            if (schema!["properties"] is not JsonObject properties)
                continue;
            foreach (var (property, value) in properties)
            {
                if (!s_compositions.Any(keyword => value![keyword] is JsonArray branches && branches.Any(b => b!["$ref"] is not null)))
                    continue;
                composed++;
                // A single reference that can be null, or SyncChangeItem.data's resource union, which also can.
                if (!IsReferenceOrNull(value!) && !(name == "SyncChangeItem" && property == "data" && IsUnionOrNull(value!)))
                    violations.Add(name + "." + property + ": " + value!.ToJsonString());
            }
        }
        Assert.True(composed >= 4, composed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [UnitFact]
    public void GivenPublishedDocument_WhenTypeArraysRead_ThenEachIsOneTypeOrNull()
    {
        var document = OpenApiBodies.Document();
        var arrays = Keywords(document, "#").Where(k => k.Name == "type" && k.Node is JsonArray).ToArray();
        Assert.NotEmpty(arrays);
        foreach (var (_, path, node) in arrays)
        {
            var types = node!.AsArray().Select(t => t!.GetValue<string>()).ToArray();
            Assert.True(types.Length == 2 && types.Count(t => t == "null") == 1, path + ": " + node.ToJsonString());
        }
    }

    // Exactly anyOf [{ $ref }, { type: "null" }], with nothing beside it.
    private static bool IsReferenceOrNull(JsonNode schema) =>
        schema.AsObject().Select(k => k.Key).SequenceEqual(["anyOf"])
        && schema["anyOf"] is JsonArray { Count: 2 } any
        && any[0]!.AsObject().Select(k => k.Key).SequenceEqual(["$ref"])
        && IsNullType(any[1]!);

    // oneOf over references, the last branch { type: "null" }, with nothing beside it.
    private static bool IsUnionOrNull(JsonNode schema) =>
        schema.AsObject().Select(k => k.Key).SequenceEqual(["oneOf"])
        && schema["oneOf"] is JsonArray { Count: > 2 } one
        && one.Take(one.Count - 1).All(b => b!.AsObject().Select(k => k.Key).SequenceEqual(["$ref"]))
        && IsNullType(one[^1]!);

    private static bool IsNullType(JsonNode schema) =>
        schema.AsObject().Select(k => k.Key).SequenceEqual(["type"]) && schema["type"] is JsonValue type && type.GetValue<string>() == "null";

    // Every keyword with its JSON Pointer. A properties map's names are property names, not keywords: they are
    // walked into but not listed.
    private static IEnumerable<(string Name, string Path, JsonNode? Node)> Keywords(JsonNode? node, string path, bool names = false)
    {
        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                foreach (var keyword in Keywords(array[index], path + "/" + index))
                    yield return keyword;
            }
        }
        else if (node is JsonObject members)
        {
            foreach (var (name, value) in members)
            {
                var child = path + "/" + name.Replace("~", "~0").Replace("/", "~1");
                if (!names)
                    yield return (name, child, value);
                foreach (var keyword in Keywords(value, child, !names && name == "properties"))
                    yield return keyword;
            }
        }
    }
}
