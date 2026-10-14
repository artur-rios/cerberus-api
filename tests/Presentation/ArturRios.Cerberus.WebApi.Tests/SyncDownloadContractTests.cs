using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;

namespace ArturRios.Cerberus.WebApi.Tests;

public class SyncDownloadContractTests
{
    [UnitFact]
    public void GivenTypedSynchronizationRoute_WhenGeneratingContract_ThenAdvertiseResetAndDisjointCompleteNativeVariants()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(Startup).Assembly.GetName().Name, Args = [] });
        builder.Services.AddCerberusContracts(); using var host = builder.Build();
        var doc = host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var route = doc.Paths["/api/sync/changes"].Operations![HttpMethod.Get];
        Assert.Null(route.RequestBody); Assert.Equal(["cursor", "pageSize", "X-Cerberus-Vault-Access"], route.Parameters!.Select(x => x.Name));
        Assert.Contains("409", route.Responses!.Keys);
        using var text = new StringWriter(); doc.SerializeAsV3(new OpenApiJsonWriter(text));
        using var contract = JsonDocument.Parse(text.ToString()); var schemas = contract.RootElement.GetProperty("components").GetProperty("schemas");
        // Assert the OAS3 wire contract: its serializer translates nullable
        // branches from the library's in-memory JSON Schema representation.
        var variants = schemas.GetProperty("SyncChangeItem").GetProperty("properties").GetProperty("data").GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.Equal(6, variants.Length);
        var empty = Assert.Single(variants, x => x.TryGetProperty("enum", out var values) && values[0].ValueKind == JsonValueKind.Null);
        Assert.Equal("object", empty.GetProperty("type").GetString()); Assert.True(empty.GetProperty("nullable").GetBoolean());
        Assert.Equal(2, schemas.GetProperty("ProfileKeyWrappers").GetProperty("properties").GetProperty("passwordWrapper").GetProperty("oneOf").GetArrayLength());
        foreach (var kind in new[] { "Profile", "Record", "Folder", "Collection", "Grant" })
        {
            var schema = schemas.GetProperty("Sync" + kind + "Data"); var properties = schema.GetProperty("properties");
            Assert.False(schema.TryGetProperty("allOf", out _)); Assert.True(properties.TryGetProperty("resourceId", out _)); Assert.True(properties.TryGetProperty("revision", out _));
            Assert.Equal(kind.ToLowerInvariant(), properties.GetProperty("resourceType").GetProperty("enum")[0].GetString());
            Assert.Contains(schema.GetProperty("required").EnumerateArray(), x => x.GetString() == "resourceType");
            Assert.True(properties.TryGetProperty(kind == "Grant" ? "recipientKeyEnvelope" : "envelope", out _));
        }
    }
}
