using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Command.Software;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Cerberus.TestSupport;

namespace ArturRios.Cerberus.Command.Tests;

public class RevokeSoftwareAccessContractTests
{
    private static readonly Guid Actor = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Grant = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private const string Handle = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly JsonSerializerOptions PermissiveNumbers = new(ProtectionFixture.Json)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    // Missing strict/required annotations or duplicate rejection would admit ambiguous revision bodies.
    [UnitTheory]
    [InlineData("{}")]
    [InlineData("{\"expectedRevision\":null}")]
    [InlineData("{\"expectedRevision\":\"1\"}")]
    [InlineData("{\"expectedRevision\":1.5}")]
    [InlineData("{\"expectedRevision\":1.0}")]
    [InlineData("{\"expectedRevision\":1e0}")]
    [InlineData("{\"expectedRevision\":1E+0}")]
    [InlineData("{\"ExpectedRevision\":1}")]
    [InlineData("{\"expectedrevision\":1}")]
    [InlineData("{\"expectedRevision\":1,\"ExpectedRevision\":1}")]
    [InlineData("{\"expectedRevision\":1,\"expectedRevision\":1}")]
    [InlineData("{\"expectedRevision\":1,\"expectedRevision\":2}")]
    [InlineData("{\"expectedRevision\":9223372036854775808}")]
    [InlineData("{\"expectedRevision\":-9223372036854775809}")]
    [InlineData("[]")]
    public void GivenMissingOrNoncanonicalRevision_WhenParsing_ThenReject(string body)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RevokeSoftwareAccessCommand>(body, ProtectionFixture.Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SoftwareGrantRevocationInput>(body, ProtectionFixture.Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RevokeSoftwareAccessCommand>(body, PermissiveNumbers));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SoftwareGrantRevocationInput>(body, PermissiveNumbers));
    }

    // Body input cannot supply trusted authority, route or software/recipient material.
    [UnitTheory]
    [InlineData("actor")]
    [InlineData("grantId")]
    [InlineData("vaultAccess")]
    [InlineData("accessVerifier")]
    [InlineData("softwareIdentity")]
    [InlineData("ownerId")]
    [InlineData("recipientKey")]
    [InlineData("keyEnvelope")]
    [InlineData("recordIds")]
    [InlineData("collectionIds")]
    [InlineData("plaintext")]
    [InlineData("unknown")]
    public void GivenForgedContextOrUnknownMember_WhenParsing_ThenReject(string member)
    {
        var body = "{\"expectedRevision\":1,\"" + member + "\":null}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RevokeSoftwareAccessCommand>(body, ProtectionFixture.Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SoftwareGrantRevocationInput>(body, ProtectionFixture.Json));
    }

    // Safe advancement rejects exhausted/unsafe revisions and a zero route, without leaking context.
    [UnitTheory]
    [InlineData(1L, true)]
    [InlineData(8L, true)]
    [InlineData(9007199254740990L, true)]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    [InlineData(long.MinValue, false)]
    [InlineData(9007199254740991L, false)]
    [InlineData(9007199254740992L, false)]
    [InlineData(long.MaxValue, false)]
    public void GivenRevision_WhenParsing_ThenExposeOnlyBodyAndValidateSafeAdvance(long revision, bool valid)
    {
        var body = "{\"expectedRevision\":" + revision.ToString(CultureInfo.InvariantCulture) + "}";
        var command = JsonSerializer.Deserialize<RevokeSoftwareAccessCommand>(body, ProtectionFixture.Json)!;
        var input = JsonSerializer.Deserialize<SoftwareGrantRevocationInput>(body, ProtectionFixture.Json)!;
        command.SetContext(Actor, Handle, Grant);
        Assert.Equal(revision, command.ExpectedRevision);
        Assert.Equal(revision, command.ToInput().ExpectedRevision);
        Assert.Equal(valid, command.ToInput().IsValid(Grant));
        Assert.Equal(valid, input.IsValid(Grant));
        Assert.False(input.IsValid(Guid.Empty));
        Assert.Equal(body, JsonSerializer.Serialize(command, ProtectionFixture.Json));
        Assert.Equal(body, JsonSerializer.Serialize(input, ProtectionFixture.Json));
    }
}
