using System.Text.Json.Serialization;

namespace ArturRios.Cerberus.Domain.Protection;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record PasswordKdf(string Algorithm, int MemoryKiB, int Iterations, int Parallelism, string Salt)
{
    public bool IsValid() => Algorithm == "argon2id-v1.3" && MemoryKiB == 65536 && Iterations == 3 && Parallelism == 4
        && ProtocolBinary.TryDecode(Salt, 16, out _);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record PasswordWrapper(string Format, long KeyEpoch, string KeySalt, string Nonce, string Ciphertext, string Tag, PasswordKdf Kdf)
{
    public bool IsValid() => Format == "cerberus-password-wrap-v1" && Kdf?.IsValid() == true
        && WrapperFields.Valid(KeyEpoch, KeySalt, Nonce, Ciphertext, Tag);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record RecoveryWrapper(string Format, long KeyEpoch, string KeySalt, string Nonce, string Ciphertext, string Tag,
    long Generation, string ProofKeyFingerprint)
{
    public bool IsValid() => Format == "cerberus-recovery-wrap-v1" && Generation is > 0 and <= ProtocolBinary.MaxInteger
        && ProtocolBinary.TryDecode(ProofKeyFingerprint, 32, out _) && WrapperFields.Valid(KeyEpoch, KeySalt, Nonce, Ciphertext, Tag);
}

internal static class WrapperFields
{
    internal static bool Valid(long epoch, string salt, string nonce, string ciphertext, string tag) =>
        epoch is > 0 and <= ProtocolBinary.MaxInteger && ProtocolBinary.TryDecode(salt, 32, out _)
        && ProtocolBinary.TryDecode(nonce, 12, out _) && ProtocolBinary.TryDecode(ciphertext, null, out _)
        && ProtocolBinary.TryDecode(tag, 16, out _);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ProtectionMaterial(PasswordWrapper PasswordWrapper, RecoveryWrapper RecoveryWrapper,
    PublicJwk UnlockVerifier, PublicJwk RecoveryVerifier, PublicJwk RecipientKey, PublicJwk AuthorKey)
{
    public bool IsValid()
    {
        if (PasswordWrapper?.IsValid() != true || RecoveryWrapper?.IsValid() != true
            || PasswordWrapper.KeyEpoch != RecoveryWrapper.KeyEpoch) return false;
        var keys = new[] { UnlockVerifier, RecoveryVerifier, RecipientKey, AuthorKey };
        return keys.All(x => x?.IsValid() == true) && keys.Select(x => x.Fingerprint()).Distinct(StringComparer.Ordinal).Count() == 4
            && RecoveryWrapper.ProofKeyFingerprint == RecoveryVerifier.Fingerprint();
    }
}
