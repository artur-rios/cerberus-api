using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Command.Collections;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Cerberus.WebApi;

namespace ArturRios.Cerberus.Command.Tests;

public class ChangeCollectionAccessContractTests
{
    private static readonly JsonSerializerOptions Json = new(ProtectionFixture.Json)
    {
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new CanonicalGuidConverter() }
    };

    // These executable RED tests start without compile-time references to the new types.
    // They protect the actual serialized command/domain boundary once it exists.
    [UnitTheory]
    [InlineData("ArturRios.Cerberus.Command.Collections.ChangeCollectionAccessCommand", true)]
    [InlineData("ArturRios.Cerberus.Domain.Collections.CollectionGrantChangeInput", false)]
    public void GivenChangeContract_WhenParsing_ThenExposeOnlyThreeRequiredBodyFields(string name, bool command)
    {
        var type = (command ? typeof(GrantCollectionAccessCommand).Assembly : typeof(CollectionGrantInput).Assembly).GetType(name);
        Assert.NotNull(type);
        using var fixture = new ProtectionFixture();
        var grant = Guid.NewGuid();
        var envelope = fixture.WrapGrant(Guid.NewGuid(), Guid.NewGuid(), grant, Guid.NewGuid(), fixture.Material.RecipientKey, revision: 2);
        var body = JsonSerializer.Serialize(new { expectedRevision = 1L, access = "read-only", keyEnvelope = envelope }, Json);
        var parsed = JsonSerializer.Deserialize(body, type, Json)!;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(parsed, type, Json));
        Assert.Equal(new[] { "access", "expectedRevision", "keyEnvelope" }, document.RootElement.EnumerateObject().Select(x => x.Name).Order());
        foreach (var field in new[] { "expectedRevision", "access", "keyEnvelope" })
        {
            var missing = JsonSerializer.SerializeToNode(parsed, type, Json)!.AsObject();
            missing.Remove(field);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(missing.ToJsonString(), type, Json));
        }
        foreach (var field in new[] { "actor", "vaultAccess", "accessVerifier", "collectionId", "grantId", "recipientAccountId", "ownerId", "profileIds", "plaintext" })
        {
            var forged = JsonSerializer.SerializeToNode(parsed, type, Json)!.AsObject();
            forged[field] = null;
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(forged.ToJsonString(), type, Json));
        }
    }
}
