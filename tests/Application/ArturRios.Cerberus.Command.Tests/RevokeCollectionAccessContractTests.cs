using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Command.Collections;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.TestSupport;

namespace ArturRios.Cerberus.Command.Tests;

public class RevokeCollectionAccessContractTests
{
    private static readonly JsonSerializerOptions Json = new(ProtectionFixture.Json)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    // Removing required/strict/unmapped annotations would admit ambiguous or forged body context.
    [UnitTheory]
    [InlineData("{}")]
    [InlineData("{\"expectedRevision\":null}")]
    [InlineData("{\"expectedRevision\":\"1\"}")]
    [InlineData("{\"expectedRevision\":1.5}")]
    [InlineData("{\"ExpectedRevision\":1}")]
    [InlineData("{\"expectedRevision\":1,\"expectedRevision\":2}")]
    [InlineData("{\"expectedRevision\":9223372036854775808}")]
    [InlineData("[]")]
    public void GivenMissingOrAmbiguousRevision_WhenParsing_ThenReject(string body)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RevokeCollectionAccessCommand>(body, Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CollectionGrantRevocationInput>(body, Json));
    }

    [UnitTheory]
    [InlineData("actor")]
    [InlineData("vaultAccess")]
    [InlineData("accessVerifier")]
    [InlineData("collectionId")]
    [InlineData("grantId")]
    [InlineData("recipientAccountId")]
    [InlineData("ownerId")]
    [InlineData("access")]
    [InlineData("keyEnvelope")]
    [InlineData("recordIds")]
    [InlineData("folderIds")]
    [InlineData("plaintext")]
    public void GivenForgedContextOrUnknownMember_WhenParsing_ThenReject(string member)
    {
        var body = "{\"expectedRevision\":1,\"" + member + "\":null}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RevokeCollectionAccessCommand>(body, Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CollectionGrantRevocationInput>(body, Json));
    }

    // Trusted context must never leak back into the caller's one-field body.
    [UnitTheory]
    [InlineData(1L, true)]
    [InlineData(8L, true)]
    [InlineData(9007199254740990L, true)]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    [InlineData(9007199254740991L, false)]
    [InlineData(9007199254740992L, false)]
    [InlineData(long.MaxValue, false)]
    public void GivenRevision_WhenParsing_ThenExposeOnlyBodyAndEnforceSafeAdvance(long revision, bool valid)
    {
        var body = "{\"expectedRevision\":" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        var command = JsonSerializer.Deserialize<RevokeCollectionAccessCommand>(body, Json)!;
        var input = JsonSerializer.Deserialize<CollectionGrantRevocationInput>(body, Json)!;
        command.SetContext(Guid.NewGuid(), "secret-handle", Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(revision, command.ExpectedRevision);
        Assert.Equal(revision, command.ToInput().ExpectedRevision);
        Assert.Equal(valid, input.IsValid(Guid.Parse("11111111-1111-4111-8111-111111111111")));
        Assert.False(input.IsValid(Guid.Empty));
        Assert.Equal(body, JsonSerializer.Serialize(command, Json));
        Assert.Equal(body, JsonSerializer.Serialize(input, Json));
    }
}
