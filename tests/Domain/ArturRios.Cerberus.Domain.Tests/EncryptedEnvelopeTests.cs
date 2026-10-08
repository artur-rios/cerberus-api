using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;

namespace ArturRios.Cerberus.Domain.Tests;

public class EncryptedEnvelopeTests
{
    [UnitFact]
    public void GivenCanonicalOpaqueEnvelope_WhenValidating_ThenAcceptWithoutDecrypting()
    {
        var envelope = Valid();
        Assert.True(envelope.IsValid());
        Assert.Equal("AQID", envelope.Ciphertext);
    }

    [UnitTheory]
    [InlineData("format", "unknown")]
    [InlineData("epoch", "0")]
    [InlineData("epoch", "-1")]
    [InlineData("nonce", "AAAAAAAAAAAAAAA")]
    [InlineData("nonce", "AAAAAAAAAAAAAAAAAA")]
    [InlineData("nonce", "AAAAAAAAAAAAAAAA=")]
    [InlineData("tag", "AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("tag", "AAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData("tag", "AAAAAAAAAAAAAAAAAAAA")]
    [InlineData("ciphertext", "")]
    [InlineData("ciphertext", "AB")]
    [InlineData("ciphertext", "AQID\n")]
    [InlineData("ciphertext", "AQ+D")]
    [InlineData("ciphertext", "A")]
    public void GivenInvalidMetadata_WhenValidating_ThenReject(string field, string value)
    {
        var envelope = Valid();
        envelope = field switch
        {
            "format" => envelope with { Format = value },
            "epoch" => envelope with { KeyEpoch = long.Parse(value) },
            "nonce" => envelope with { Nonce = value },
            "tag" => envelope with { Tag = value },
            _ => envelope with { Ciphertext = value }
        };
        Assert.False(envelope.IsValid());
    }

    [UnitTheory]
    [InlineData("privateKey")]
    [InlineData("plaintext")]
    [InlineData("ownerId")]
    public void GivenUnknownEnvelopeMember_WhenDeserializing_ThenReject(string member)
    {
        var json = "{\"format\":\"cerberus-aes256gcm-v1\",\"keyEpoch\":1,\"nonce\":\"AAAAAAAAAAAAAAAA\",\"ciphertext\":\"AQID\",\"tag\":\"AAAAAAAAAAAAAAAAAAAAAA\",\"" + member + "\":\"forbidden\"}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EncryptedEnvelope>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static EncryptedEnvelope Valid() => new("cerberus-aes256gcm-v1", 1,
        "AAAAAAAAAAAAAAAA", "AQID", "AAAAAAAAAAAAAAAAAAAAAA");
}
