using System.Text.Json;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;

namespace ArturRios.Cerberus.Domain.Tests;

public class ProtectionContractTests
{
    [UnitFact]
    public void GivenSeparatePublicKeysAndOpaqueWrappers_WhenValidating_ThenAcceptWithoutPlaintext()
    {
        using var f = new ProtectionFixture();
        Assert.True(f.Material.IsValid());
        Assert.Equal(f.Material.RecoveryVerifier.Fingerprint(), f.Material.RecoveryWrapper.ProofKeyFingerprint);
    }

    [UnitTheory]
    [InlineData("passwordFormat")][InlineData("recoveryFormat")][InlineData("zeroEpoch")]
    [InlineData("unsafeEpoch")][InlineData("differentEpoch")][InlineData("keySalt")]
    [InlineData("nonce")][InlineData("tag")][InlineData("ciphertext")][InlineData("paddedCiphertext")]
    [InlineData("kdfAlgorithm")][InlineData("kdfMemory")][InlineData("kdfIterations")][InlineData("kdfLanes")][InlineData("kdfSalt")]
    [InlineData("nullKdf")][InlineData("nullWrapper")][InlineData("zeroGeneration")][InlineData("fingerprint")]
    [InlineData("nullKey")][InlineData("offCurve")][InlineData("kty")][InlineData("curve")]
    public void GivenInvalidProtectionMetadata_WhenValidating_ThenReject(string invalid)
    {
        using var f = new ProtectionFixture(); var m = f.Material;
        var p = m.PasswordWrapper; var r = m.RecoveryWrapper;
        m = invalid switch
        {
            "passwordFormat" => m with { PasswordWrapper = p with { Format = "unsupported" } },
            "recoveryFormat" => m with { RecoveryWrapper = r with { Format = "unsupported" } },
            "zeroEpoch" => m with { PasswordWrapper = p with { KeyEpoch = 0 } },
            "unsafeEpoch" => m with { PasswordWrapper = p with { KeyEpoch = 9007199254740992 } },
            "differentEpoch" => m with { RecoveryWrapper = r with { KeyEpoch = 2 } },
            "keySalt" => m with { PasswordWrapper = p with { KeySalt = "AQID" } },
            "nonce" => m with { PasswordWrapper = p with { Nonce = "AQID" } },
            "tag" => m with { RecoveryWrapper = r with { Tag = "AQID" } },
            "ciphertext" => m with { PasswordWrapper = p with { Ciphertext = "" } },
            "paddedCiphertext" => m with { RecoveryWrapper = r with { Ciphertext = "AQI=" } },
            "kdfAlgorithm" => m with { PasswordWrapper = p with { Kdf = p.Kdf with { Algorithm = "argon2i" } } },
            "kdfMemory" => m with { PasswordWrapper = p with { Kdf = p.Kdf with { MemoryKiB = 1 } } },
            "kdfIterations" => m with { PasswordWrapper = p with { Kdf = p.Kdf with { Iterations = 1 } } },
            "kdfLanes" => m with { PasswordWrapper = p with { Kdf = p.Kdf with { Parallelism = 1 } } },
            "kdfSalt" => m with { PasswordWrapper = p with { Kdf = p.Kdf with { Salt = "AQID" } } },
            "nullKdf" => m with { PasswordWrapper = p with { Kdf = null! } },
            "nullWrapper" => m with { RecoveryWrapper = null! },
            "zeroGeneration" => m with { RecoveryWrapper = r with { Generation = 0 } },
            "fingerprint" => m with { RecoveryWrapper = r with { ProofKeyFingerprint = ProtectionFixture.Encode(new byte[32]) } },
            "nullKey" => m with { UnlockVerifier = null! },
            "offCurve" => m with { AuthorKey = m.AuthorKey with { X = ProtectionFixture.Encode(new byte[32]), Y = ProtectionFixture.Encode(new byte[32]) } },
            "kty" => m with { RecipientKey = m.RecipientKey with { Kty = "RSA" } },
            "curve" => m with { UnlockVerifier = m.UnlockVerifier with { Crv = "P-384" } },
            _ => throw new InvalidOperationException()
        };
        Assert.False(m.IsValid());
    }

    [UnitTheory]
    [InlineData(0,1)][InlineData(0,2)][InlineData(0,3)][InlineData(1,2)][InlineData(1,3)][InlineData(2,3)]
    public void GivenOneKeyReusedAcrossRoles_WhenValidating_ThenReject(int source, int target)
    {
        using var f = new ProtectionFixture();
        var keys = new[] { f.Material.UnlockVerifier, f.Material.RecoveryVerifier, f.Material.RecipientKey, f.Material.AuthorKey };
        keys[target] = keys[source];
        var m = f.Material with { UnlockVerifier = keys[0], RecoveryVerifier = keys[1], RecipientKey = keys[2], AuthorKey = keys[3],
            RecoveryWrapper = f.Material.RecoveryWrapper with { ProofKeyFingerprint = keys[1].Fingerprint() } };
        Assert.False(m.IsValid());
    }

    [UnitTheory]
    [InlineData("private")][InlineData("unknown")][InlineData("duplicate")][InlineData("stringInteger")][InlineData("decimalInteger")]
    public void GivenUnknownPrivateOrCoercedFields_WhenParsing_ThenReject(string invalid)
    {
        using var f = new ProtectionFixture();
        var raw = JsonSerializer.Serialize(f.Material, ProtectionFixture.Json);
        raw = invalid switch
        {
            "private" => raw.Replace("\"crv\":", "\"d\":\"private\",\"crv\":"),
            "unknown" => raw.Insert(1,"\"rawPassword\":\"secret\","),
            "duplicate" => raw.Replace("\"keyEpoch\":1", "\"keyEpoch\":1,\"keyEpoch\":1"),
            "stringInteger" => raw.Replace("\"keyEpoch\":1", "\"keyEpoch\":\"1\""),
            _ => raw.Replace("\"keyEpoch\":1", "\"keyEpoch\":1.0")
        };
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProtectionMaterial>(raw, ProtectionFixture.Json));
    }
}
