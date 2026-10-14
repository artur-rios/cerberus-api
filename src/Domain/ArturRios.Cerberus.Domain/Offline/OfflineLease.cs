using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Offline;

public sealed record OfflineGrantRevision(Guid GrantId, long Revision);
public sealed record OfflineLeaseSnapshot(Guid IdentityId, Guid AccountId, Guid ProfileId,
    long KeyEpoch, long ProtectionRevision, long PolicyRevision, long RevocationGeneration,
    OfflineGrantRevision[] GrantRevisions, bool RenewalEnabled, TimeSpan? RenewalInterval, DateTimeOffset IssuedAt)
{
    public bool IsValid() => IdentityId != Guid.Empty && AccountId != Guid.Empty && ProfileId != Guid.Empty
        && Safe(KeyEpoch) && Safe(ProtectionRevision) && Safe(PolicyRevision) && Safe(RevocationGeneration)
        && GrantRevisions is not null && GrantRevisions.All(g => g is not null && g.GrantId != Guid.Empty && Safe(g.Revision))
        && GrantRevisions.Select(g => g.GrantId).Distinct().Count() == GrantRevisions.Length
        && IssuedAt.Offset == TimeSpan.Zero && IssuedAt.ToUnixTimeSeconds() >= 0
        && (RenewalEnabled ? RenewalInterval is { Ticks: > 0 } duration
            && duration.Ticks % TimeSpan.TicksPerSecond == 0 && duration <= DateTimeOffset.MaxValue - IssuedAt
            : RenewalInterval is null);
    private static bool Safe(long n) => n is > 0 and <= ProtocolBinary.MaxInteger;
}
public sealed record SignedOfflineLease(string Compact, DateTimeOffset IssuedAt, DateTimeOffset? ExpiresAt)
{
    // Internal result consistency only. Client authentication still requires the
    // signature to verify against separately provisioned issuer-bound trust.
    public bool MatchesMetadata(OfflineLeaseSnapshot s)
    {
        if (s?.IsValid() != true || string.IsNullOrEmpty(Compact) || Compact.Length > 1048576) return false;
        var issued = s.IssuedAt.ToUnixTimeSeconds();
        var expiry = s.RenewalEnabled ? issued + s.RenewalInterval!.Value.Ticks / TimeSpan.TicksPerSecond : (long?)null;
        if (IssuedAt != DateTimeOffset.FromUnixTimeSeconds(issued)
            || ExpiresAt != (expiry is { } endTime ? DateTimeOffset.FromUnixTimeSeconds(endTime) : null)) return false;
        var parts = Compact.Split('.');
        if (parts.Length != 3 || !ProtocolBinary.TryDecode(parts[0], null, out var header)
            || !ProtocolBinary.TryDecode(parts[1], null, out var payload) || !ProtocolBinary.TryDecode(parts[2], 64, out _)) return false;
        try
        {
            using var h = JsonDocument.Parse(header); using var p = JsonDocument.Parse(payload);
            var head = h.RootElement; var c = p.RootElement;
            if (!Fields(head, ["alg", "typ", "kid"]) || head.GetProperty("alg").GetString() != "ES256"
                || head.GetProperty("typ").GetString() != OfflineLease.Type
                || !ProtocolBinary.TryDecode(head.GetProperty("kid").GetString(), 32, out _)) return false;
            var required = new HashSet<string> { "iss", "aud", "sub", "accountId", "scopeKind", "scopeId", "keyEpoch", "protectionRevision",
                "policyRevision", "revocationGeneration", "grantRevisions", "renewalEnabled", "iat", "nbf", "jti" };
            if (expiry is not null) required.Add("exp");
            if (!Fields(c, required) || !OfflineLease.ValidIssuer(c.GetProperty("iss").GetString())
                || c.GetProperty("aud").GetString() != OfflineLease.Audience || c.GetProperty("sub").GetString() != s.IdentityId.ToString("D")
                || c.GetProperty("accountId").GetString() != s.AccountId.ToString("D") || c.GetProperty("scopeKind").GetString() != "profile"
                || c.GetProperty("scopeId").GetString() != s.ProfileId.ToString("D") || c.GetProperty("keyEpoch").GetInt64() != s.KeyEpoch
                || c.GetProperty("protectionRevision").GetInt64() != s.ProtectionRevision || c.GetProperty("policyRevision").GetInt64() != s.PolicyRevision
                || c.GetProperty("revocationGeneration").GetInt64() != s.RevocationGeneration || c.GetProperty("renewalEnabled").GetBoolean() != s.RenewalEnabled
                || c.GetProperty("iat").GetInt64() != issued || c.GetProperty("nbf").GetInt64() != issued
                || expiry is { } end && c.GetProperty("exp").GetInt64() != end) return false;
            var jti = c.GetProperty("jti").GetString();
            if (!Guid.TryParseExact(jti, "D", out var id) || id == Guid.Empty || jti != id.ToString("D")) return false;
            var expected = s.GrantRevisions.OrderBy(g => g.GrantId.ToString("D"), StringComparer.Ordinal).ToArray();
            var grants = c.GetProperty("grantRevisions").EnumerateArray().ToArray();
            return grants.Length == expected.Length && grants.Select((g, i) => Fields(g, ["grantId", "revision"])
                && g.GetProperty("grantId").GetString() == expected[i].GrantId.ToString("D") && g.GetProperty("revision").GetInt64() == expected[i].Revision).All(x => x);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException) { return false; }
    }
    private static bool Fields(JsonElement value, HashSet<string> expected) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Count() == expected.Count && value.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(expected);
}
public interface IOfflineLeaseSigner
{
    SignedOfflineLease? Sign(OfflineLeaseSnapshot snapshot);
}

public static class OfflineLease
{
    public const string Audience = "cerberus-offline-clients-v1";
    public const string Type = "cerberus-offline-v1+jwt";
    public static bool ValidIssuer(string? issuer) => !string.IsNullOrWhiteSpace(issuer)
        && issuer.IsNormalized(NormalizationForm.FormC) && issuer.All(c => c <= 127 && !char.IsControl(c));
    public static bool TrySigningPublicKey(string? pem, out PublicJwk? publicKey)
    {
        publicKey = null;
        if (string.IsNullOrWhiteSpace(pem)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            var p = key.ExportParameters(true);
            if (p.D is not { Length: 32 } || p.Curve.Oid.Value != "1.2.840.10045.3.1.7"
                || p.Q.X is not { Length: 32 } || p.Q.Y is not { Length: 32 }) return false;
            publicKey = new("P-256", "EC", ProtocolBinary.Encode(p.Q.X), ProtocolBinary.Encode(p.Q.Y));
            return publicKey.IsValid();
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { return false; }
    }

    public static SignedOfflineLease Sign(OfflineLeaseSnapshot snapshot, string issuer, string privatePem)
    {
        if (snapshot?.IsValid() != true || !ValidIssuer(issuer) || !TrySigningPublicKey(privatePem, out var publicKey))
            throw new ArgumentException("Invalid offline lease configuration or snapshot.");
        var issued = snapshot.IssuedAt.ToUnixTimeSeconds();
        long? expires = snapshot.RenewalEnabled ? checked(issued + snapshot.RenewalInterval!.Value.Ticks / TimeSpan.TicksPerSecond) : null;
        var claims = new Dictionary<string, object>
        {
            ["iss"] = issuer, ["aud"] = Audience, ["sub"] = snapshot.IdentityId.ToString("D"),
            ["accountId"] = snapshot.AccountId.ToString("D"), ["scopeKind"] = "profile", ["scopeId"] = snapshot.ProfileId.ToString("D"),
            ["keyEpoch"] = snapshot.KeyEpoch, ["protectionRevision"] = snapshot.ProtectionRevision,
            ["policyRevision"] = snapshot.PolicyRevision, ["revocationGeneration"] = snapshot.RevocationGeneration,
            ["grantRevisions"] = snapshot.GrantRevisions.OrderBy(g => g.GrantId.ToString("D"), StringComparer.Ordinal)
                .Select(g => new { grantId = g.GrantId.ToString("D"), revision = g.Revision }).ToArray(),
            ["renewalEnabled"] = snapshot.RenewalEnabled, ["iat"] = issued, ["nbf"] = issued, ["jti"] = Guid.NewGuid().ToString("D")
        };
        if (expires is { } expiry) claims.Add("exp", expiry);
        var header = new { alg = "ES256", typ = Type, kid = publicKey!.Fingerprint() };
        var prefix = ProtocolBinary.Encode(JsonSerializer.SerializeToUtf8Bytes(header)) + "."
            + ProtocolBinary.Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        using var key = ECDsa.Create();
        key.ImportFromPem(privatePem);
        var signature = key.SignData(Encoding.ASCII.GetBytes(prefix), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var compact = prefix + "." + ProtocolBinary.Encode(signature);
        if (compact.Length > 1048576) throw new ArgumentException("Offline lease exceeds the supported wire limit.");
        return new(compact, DateTimeOffset.FromUnixTimeSeconds(issued), expires is { } end ? DateTimeOffset.FromUnixTimeSeconds(end) : null);
    }
}
