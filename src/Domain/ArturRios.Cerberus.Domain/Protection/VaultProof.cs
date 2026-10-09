using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArturRios.Cerberus.Domain.Protection;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record VaultProofBinding(string Operation, Guid IdentityId, Guid AccountId, string ScopeKind, Guid ScopeId,
    long KeyEpoch, long ProtectionRevision, long? Generation);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record VaultProofChallenge(string Format, Guid ChallengeId, string Nonce, string Operation, Guid IdentityId,
    Guid AccountId, string ScopeKind, Guid ScopeId, long KeyEpoch, long ProtectionRevision, long? Generation,
    string RequestHash, long IssuedAt, long ExpiresAt)
{
    public VaultProofBinding Binding() => new(Operation, IdentityId, AccountId, ScopeKind, ScopeId, KeyEpoch, ProtectionRevision, Generation);
}

public static class VaultProof
{
    public static byte[] SigningBytes(VaultProofChallenge value) => JsonSerializer.SerializeToUtf8Bytes(new object?[]
    { "cerberus-proof-v1", value.ChallengeId.ToString("D"), value.Nonce, value.Operation, value.IdentityId.ToString("D"),
        value.AccountId.ToString("D"), value.ScopeKind, value.ScopeId.ToString("D"), value.KeyEpoch, value.ProtectionRevision,
        value.Generation, value.RequestHash, value.IssuedAt, value.ExpiresAt });

    public static bool Verify(VaultProofChallenge value, VaultProofBinding expected, byte[] rawBody, PublicJwk key, string proof, long now)
    {
        if (!Valid(value) || value.Binding() != expected || now < value.IssuedAt || now >= value.ExpiresAt
            || !ProtocolBinary.TryDecode(proof, 64, out var signature) || !RawObject(rawBody)
            || value.RequestHash != ProtocolBinary.Encode(SHA256.HashData(rawBody))) return false;
        return key.Verify(SigningBytes(value), signature);
    }

    private static bool Valid(VaultProofChallenge value) => value.Format == "cerberus-challenge-v1"
        && value.ChallengeId != Guid.Empty && value.IdentityId != Guid.Empty && value.AccountId != Guid.Empty && value.ScopeId != Guid.Empty
        && value.Operation is "unlock-account" or "unlock-profile" or "change-protection" or "recover" or "refresh-recovery"
        && value.ScopeKind is "account" or "profile" && (value.ScopeKind != "account" || value.ScopeId == value.AccountId)
        && (value.Operation != "unlock-account" || value.ScopeKind == "account")
        && (value.Operation != "unlock-profile" || value.ScopeKind == "profile")
        && (value.Operation == "recover" ? value.ScopeKind == "account" && value.Generation is > 0 and <= ProtocolBinary.MaxInteger : value.Generation is null)
        && value.KeyEpoch is > 0 and <= ProtocolBinary.MaxInteger && value.ProtectionRevision is > 0 and <= ProtocolBinary.MaxInteger
        && value.IssuedAt is >= 0 and <= ProtocolBinary.MaxInteger - 60 && value.ExpiresAt == value.IssuedAt + 60
        && ProtocolBinary.TryDecode(value.Nonce, 32, out _) && ProtocolBinary.TryDecode(value.RequestHash, 32, out _);

    private static bool RawObject(byte[] raw)
    {
        if (raw.Length == 0 || raw.Length > 1048576) return false;
        try
        {
            _ = new UTF8Encoding(false, true).GetString(raw);
            using var body = JsonDocument.Parse(raw, new JsonDocumentOptions { AllowDuplicateProperties = false });
            return body.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException) { return false; }
    }
}
