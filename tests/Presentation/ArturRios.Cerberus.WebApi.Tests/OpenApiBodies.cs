using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;

namespace ArturRios.Cerberus.WebApi.Tests;

// Checks a JSON body the server wrote against the response schema the published document gives for it.
// It reads the document as JSON, as a generated client does, and knows the parts of JSON Schema the
// document uses: $ref, allOf, anyOf, oneOf, type (one, or OpenAPI 3.1's array with "null"), OpenAPI 3.0's
// nullable, enum, properties, required, additionalProperties and items.
internal static class OpenApiBodies
{
    // The contract's services, as tools/OpenApiGen builds them.
    public static WebApplication Host()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(Startup).Assembly.GetName().Name, Args = [] });
        builder.Services.AddCerberusContracts();
        return builder.Build();
    }

    // The document scripts/openapi.py writes to docs/contracts/openapi.json, generated in process.
    public static JsonNode Document()
    {
        using var host = Host();
        return Document(host);
    }

    public static JsonNode Document(WebApplication host)
    {
        var document = host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        using var text = new StringWriter();
        document.SerializeAsV3(new OpenApiJsonWriter(text));
        return JsonNode.Parse(text.ToString())!;
    }

    // Every operation's documented responses: (method, path, status, response).
    public static IEnumerable<(string Method, string Path, string Status, JsonObject Response)> Responses(JsonNode document) =>
        document["paths"]!.AsObject().SelectMany(path => path.Value!.AsObject()
            .SelectMany(operation => operation.Value!["responses"]!.AsObject()
                .Select(response => (operation.Key.ToUpperInvariant(), path.Key, response.Key, response.Value!.AsObject()))));

    // Null when the schema accepts the body, else why not.
    public static string? Reject(JsonNode document, JsonNode schema, string body) =>
        Reject(document, schema, JsonNode.Parse(body), "$");

    private static string? Reject(JsonNode document, JsonNode schema, JsonNode? value, string at)
    {
        if (schema["$ref"]?.GetValue<string>() is { } reference)
            return Reject(document, Resolve(document, reference), value, at);
        if (schema["allOf"] is JsonArray all && all.Select(branch => Reject(document, branch!, value, at)).FirstOrDefault(why => why is not null) is { } failed)
            return failed;
        if (schema["anyOf"] is JsonArray any && any.All(branch => Reject(document, branch!, value, at) is not null))
            return at + ": no anyOf branch accepts " + Text(value);
        if (schema["oneOf"] is JsonArray one && one.Count(branch => Reject(document, branch!, value, at) is null) != 1)
            return at + ": not exactly one oneOf branch accepts " + Text(value);
        if (schema["enum"] is JsonArray values && !values.Any(option => JsonNode.DeepEquals(option, value)))
            return at + ": " + Text(value) + " is not in the enum";
        if (!TypeAccepts(schema, value))
            return at + ": " + Text(value) + " is not " + Text(schema["type"]) + (schema["nullable"] is null ? "" : " (nullable)");
        if (value is JsonObject members)
        {
            var properties = schema["properties"]?.AsObject();
            foreach (var name in schema["required"]?.AsArray().Select(item => item!.GetValue<string>()) ?? [])
            {
                if (!members.ContainsKey(name))
                    return at + "." + name + " is required";
            }
            foreach (var (name, member) in members)
            {
                if (properties?[name] is { } property)
                {
                    if (Reject(document, property, member, at + "." + name) is { } why)
                        return why;
                }
                else if (schema["additionalProperties"] is JsonValue additional && !additional.GetValue<bool>())
                    return at + "." + name + " is not a documented member";
            }
        }
        if (value is JsonArray items && schema["items"] is { } item)
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (Reject(document, item, items[index], at + "[" + index + "]") is { } why)
                    return why;
            }
        }
        return null;
    }

    private static bool TypeAccepts(JsonNode schema, JsonNode? value)
    {
        string[] types = schema["type"] switch
        {
            JsonArray array => [.. array.Select(item => item!.GetValue<string>())],
            JsonValue one => [one.GetValue<string>()],
            _ => []
        };
        if (value is null)
            return types.Length == 0 || types.Contains("null") || schema["nullable"]?.GetValue<bool>() == true;
        if (types.Length == 0)
            return true;
        var kind = value.GetValueKind();
        return types.Any(type => type switch
        {
            "object" => kind == JsonValueKind.Object,
            "array" => kind == JsonValueKind.Array,
            "string" => kind == JsonValueKind.String,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "integer" => kind == JsonValueKind.Number && value.GetValue<decimal>() % 1 == 0,
            "number" => kind == JsonValueKind.Number,
            _ => false
        });
    }

    private static JsonNode Resolve(JsonNode document, string reference) =>
        reference.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries).Aggregate(document, (node, part) => node[part]!);

    private static string Text(JsonNode? value) => value?.ToJsonString() ?? "null";
}
