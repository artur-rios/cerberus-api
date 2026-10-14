using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Offline;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Tests;

public class OfflineLeaseTests
{
    private static OfflineLeaseSnapshot Snapshot(bool enabled = true, TimeSpan? duration = null) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, 3, 4, 5, [], enabled,
        enabled ? duration ?? TimeSpan.FromHours(24) : null, DateTimeOffset.FromUnixTimeSeconds(1000));

    [UnitTheory]
    [InlineData(true, 86400)]
    [InlineData(true, 1)]
    [InlineData(true, 123456)]
    [InlineData(false, 0)]
    public void GivenCurrentProfileSnapshot_WhenSigning_ThenBindNativeClaimsAndVerifyOriginalSignature(bool enabled, long seconds)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var snapshot = Snapshot(enabled, enabled ? TimeSpan.FromSeconds(seconds) : null);
        var lease = OfflineLease.Sign(snapshot, "fixture-lease-issuer", key.ExportPkcs8PrivateKeyPem());
        var parts = lease.Compact.Split('.');
        Assert.Equal(3, parts.Length);
        using var header = Decode(parts[0]);
        Assert.Equal(3, header.RootElement.EnumerateObject().Count());
        Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("cerberus-offline-v1+jwt", header.RootElement.GetProperty("typ").GetString());
        Assert.True(OfflineLease.TrySigningPublicKey(key.ExportPkcs8PrivateKeyPem(), out var publicKey));
        Assert.Equal(publicKey!.Fingerprint(), header.RootElement.GetProperty("kid").GetString());
        using var claims = Decode(parts[1]);
        var c = claims.RootElement;
        Assert.Equal("fixture-lease-issuer", c.GetProperty("iss").GetString());
        Assert.Equal("cerberus-offline-clients-v1", c.GetProperty("aud").GetString());
        Assert.Equal(snapshot.IdentityId, c.GetProperty("sub").GetGuid());
        Assert.Equal(snapshot.AccountId, c.GetProperty("accountId").GetGuid());
        Assert.Equal("profile", c.GetProperty("scopeKind").GetString());
        Assert.Equal(snapshot.ProfileId, c.GetProperty("scopeId").GetGuid());
        Assert.Equal(2, c.GetProperty("keyEpoch").GetInt64());
        Assert.Equal(3, c.GetProperty("protectionRevision").GetInt64());
        Assert.Equal(4, c.GetProperty("policyRevision").GetInt64());
        Assert.Equal(5, c.GetProperty("revocationGeneration").GetInt64());
        Assert.Empty(c.GetProperty("grantRevisions").EnumerateArray());
        Assert.Equal(enabled, c.GetProperty("renewalEnabled").GetBoolean());
        Assert.Equal(1000, c.GetProperty("iat").GetInt64());
        Assert.Equal(1000, c.GetProperty("nbf").GetInt64());
        Assert.NotEqual(Guid.Empty, c.GetProperty("jti").GetGuid());
        Assert.Equal(enabled, c.TryGetProperty("exp", out var expiry));
        if (enabled) Assert.Equal(1000 + seconds, expiry.GetInt64());
        Assert.Equal(enabled ? snapshot.IssuedAt.AddSeconds(seconds) : null, lease.ExpiresAt);
        Assert.True(ProtocolBinary.TryDecode(parts[2], 64, out var signature));
        Assert.True(publicKey.Verify(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature));
        signature[0] ^= 1;
        Assert.False(publicKey.Verify(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature));
        Assert.NotEqual(lease.Compact, OfflineLease.Sign(snapshot, "fixture-lease-issuer", key.ExportPkcs8PrivateKeyPem()).Compact);
    }

    [UnitTheory]
    [InlineData("identity")][InlineData("account")][InlineData("profile")][InlineData("epoch")]
    [InlineData("revision")][InlineData("policy")][InlineData("generation")][InlineData("safeCounter")]
    [InlineData("zeroDuration")][InlineData("fraction")][InlineData("disabledInterval")][InlineData("overflow")]
    [InlineData("duplicateGrant")][InlineData("grantId")][InlineData("grantRevision")][InlineData("beforeEpoch")]
    public void GivenInvalidSnapshot_WhenSigning_ThenReject(string invalid)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var s = Snapshot();
        var grant = new OfflineGrantRevision(Guid.NewGuid(), 1);
        s = invalid switch
        {
            "identity" => s with { IdentityId = Guid.Empty }, "account" => s with { AccountId = Guid.Empty },
            "profile" => s with { ProfileId = Guid.Empty }, "epoch" => s with { KeyEpoch = 0 },
            "revision" => s with { ProtectionRevision = 0 }, "policy" => s with { PolicyRevision = 0 },
            "generation" => s with { RevocationGeneration = 0 }, "safeCounter" => s with { PolicyRevision = ProtocolBinary.MaxInteger + 1 },
            "zeroDuration" => s with { RenewalInterval = TimeSpan.Zero }, "fraction" => s with { RenewalInterval = TimeSpan.FromTicks(1) },
            "disabledInterval" => s with { RenewalEnabled = false }, "overflow" => s with { RenewalInterval = TimeSpan.MaxValue },
            "duplicateGrant" => s with { GrantRevisions = [grant, grant] }, "grantId" => s with { GrantRevisions = [grant with { GrantId = Guid.Empty }] },
            "grantRevision" => s with { GrantRevisions = [grant with { Revision = 0 }] },
            _ => s with { IssuedAt = DateTimeOffset.FromUnixTimeSeconds(-1) }
        };
        Assert.Throws<ArgumentException>(() => OfflineLease.Sign(s, "fixture-lease-issuer", key.ExportPkcs8PrivateKeyPem()));
    }

    [UnitFact]
    public void GivenUnsortedGrants_WhenSigning_ThenUseCanonicalUniqueGrantOrder()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var a = new OfflineGrantRevision(Guid.Parse("00000001-0000-0000-0000-000000000001"), 2);
        var b = new OfflineGrantRevision(Guid.Parse("00000002-0000-0000-0000-000000000002"), 3);
        using var claims = Decode(OfflineLease.Sign(Snapshot() with { GrantRevisions = [b, a] }, "fixture-lease-issuer", key.ExportPkcs8PrivateKeyPem()).Compact.Split('.')[1]);
        Assert.Equal(new[] { a.GrantId, b.GrantId }, claims.RootElement.GetProperty("grantRevisions").EnumerateArray().Select(x => x.GetProperty("grantId").GetGuid()));
    }

    [UnitFact]
    public void GivenUnsupportedOrPublicOnlyKey_WhenCheckingSigningConfiguration_ThenReject()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.False(OfflineLease.TrySigningPublicKey(null, out _));
        Assert.False(OfflineLease.TrySigningPublicKey("invalid", out _));
        Assert.False(OfflineLease.TrySigningPublicKey(key.ExportSubjectPublicKeyInfoPem(), out _));
        Assert.False(OfflineLease.TrySigningPublicKey(wrong.ExportPkcs8PrivateKeyPem(), out _));
    }
    private static JsonDocument Decode(string part)
    { Assert.True(ProtocolBinary.TryDecode(part, null, out var bytes)); return JsonDocument.Parse(bytes); }
}
