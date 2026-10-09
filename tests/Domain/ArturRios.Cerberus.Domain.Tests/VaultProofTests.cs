using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;

namespace ArturRios.Cerberus.Domain.Tests;

public class VaultProofTests
{
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"vault-proof-vector.json")));
    [UnitFact]
    public void GivenExistingNativePythonProofVector_WhenVerifying_ThenMatchExactWireAndAcceptSignature()
    {
        using var f = Fixture(); var root = f.RootElement;
        var challenge = root.GetProperty("challenge").Deserialize<VaultProofChallenge>(ProtectionFixture.Json)!;
        var binding = root.GetProperty("binding").Deserialize<VaultProofBinding>(ProtectionFixture.Json)!;
        var key = root.GetProperty("publicJwk").Deserialize<PublicJwk>(ProtectionFixture.Json)!;
        Assert.Equal(ProtectionFixture.Decode(root.GetProperty("signingBytes").GetString()!), VaultProof.SigningBytes(challenge));
        Assert.True(VaultProof.Verify(challenge,binding,ProtectionFixture.Decode(root.GetProperty("rawBody").GetString()!),key,root.GetProperty("signature").GetString()!,1000));
    }

    [UnitTheory]
    [InlineData("actor")][InlineData("account")][InlineData("scope")][InlineData("operation")][InlineData("epoch")][InlineData("revision")]
    [InlineData("generation")][InlineData("nonce")][InlineData("id")][InlineData("hash")][InlineData("format")]
    [InlineData("expiry")][InlineData("future")][InlineData("body")][InlineData("signature")][InlineData("key")]
    public void GivenDifferentBindingBodyTimeOrSignature_WhenVerifying_ThenReject(string invalid)
    {
        using var f = Fixture(); var root = f.RootElement;
        var c = root.GetProperty("challenge").Deserialize<VaultProofChallenge>(ProtectionFixture.Json)!;
        var b = root.GetProperty("binding").Deserialize<VaultProofBinding>(ProtectionFixture.Json)!;
        var key = root.GetProperty("publicJwk").Deserialize<PublicJwk>(ProtectionFixture.Json)!;
        var raw = ProtectionFixture.Decode(root.GetProperty("rawBody").GetString()!);
        var signature = root.GetProperty("signature").GetString()!;
        c = invalid switch
        {
            "actor" => c with { IdentityId = Guid.NewGuid() }, "account" => c with { AccountId = Guid.NewGuid() },
            "scope" => c with { ScopeId = Guid.NewGuid() }, "operation" => c with { Operation = "unlock-profile" },
            "epoch" => c with { KeyEpoch = 2 }, "revision" => c with { ProtectionRevision = 2 },
            "generation" => c with { Generation = 1 }, "nonce" => c with { Nonce = ProtectionFixture.Encode(new byte[32]) },
            "id" => c with { ChallengeId = Guid.Empty }, "hash" => c with { RequestHash = ProtectionFixture.Encode(new byte[32]) },
            "format" => c with { Format = "unsupported" }, _ => c
        };
        if (invalid == "body") raw = Encoding.UTF8.GetBytes("{ \"expectedProtectionRevision\":1}");
        if (invalid == "signature") signature = ProtectionFixture.Encode(new byte[64]);
        if (invalid == "key") { using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256); key = ProtectionFixture.Public(wrong); }
        Assert.False(VaultProof.Verify(c,b,raw,key,signature,invalid == "expiry" ? 1060 : invalid == "future" ? 999 : 1000));
    }

    [UnitFact]
    public void GivenRfc6979PublishedP256Signature_WhenVerifying_ThenAcceptAndRejectMutation()
    {
        var key = new PublicJwk("P-256","EC",ProtectionFixture.Encode(Convert.FromHexString("60FED4BA255A9D31C961EB74C6356D68C049B8923B61FA6CE669622E60F29FB6")),
            ProtectionFixture.Encode(Convert.FromHexString("7903FE1008B8BC99A41AE9E95628BC64F2F1B20C2D7E9F5177A3C294D4462299")));
        var signature = Convert.FromHexString("EFD48B2AACB6A8FD1140DD9CD45E81D69D2C877B56AAF991C34D0EA84EAF3716F7CB1C942D657C41D436C7A1B6E29F65F3E900DBB9AFF4064DC4AB2F843ACDA8");
        Assert.True(key.Verify(Encoding.ASCII.GetBytes("sample"),signature));
        signature[0] ^= 1;
        Assert.False(key.Verify(Encoding.ASCII.GetBytes("sample"),signature));
        Assert.False(key.Verify(Encoding.ASCII.GetBytes("sample"),new byte[63]));
    }
}
