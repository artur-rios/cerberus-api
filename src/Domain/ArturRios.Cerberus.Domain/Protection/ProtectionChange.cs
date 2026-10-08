using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;

namespace ArturRios.Cerberus.Domain.Protection;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ContentReplacement(string ResourceKind, Guid ResourceId, long ExpectedRevision, EncryptedEnvelope Envelope, ProfileKeyWrappers? KeyWrappers = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ProtectionChange(Guid AccountId, long ExpectedProtectionRevision, long ExpectedAccountRevision,
    string Mode, ProtectionMaterial Material, ContentReplacement[] ContentReplacements)
{
    public bool IsValid() => AccountId != Guid.Empty
        && ExpectedProtectionRevision is > 0 and <= ProtocolBinary.MaxInteger
        && ExpectedAccountRevision is > 0 and <= ProtocolBinary.MaxInteger
        && Material?.IsValid() == true && ContentReplacements is not null
        && (Mode == "rewrap" ? ContentReplacements.Length == 0
            : Mode == "rotate-content" && ContentReplacements.Length > 0
                && ContentReplacements.All(item => item is not null && item.ResourceId != Guid.Empty
                    && item.ExpectedRevision is > 0 and <= ProtocolBinary.MaxInteger && item.Envelope?.IsValid() == true
                    && (item.ResourceKind == "account" ? item.ResourceId == AccountId && item.ExpectedRevision == ExpectedAccountRevision && item.KeyWrappers is null
                        : item.ResourceKind == "profile" && item.KeyWrappers?.IsValid() == true))
                && ContentReplacements.Count(item => item.ResourceKind == "account") == 1
                && ContentReplacements.Select(item => (item.ResourceKind,item.ResourceId)).Distinct().Count() == ContentReplacements.Length);


    public bool IsValidTransition(ProtectionMaterial current) => IsValid() && current?.IsValid() == true
        && current.PasswordWrapper.KeyEpoch < ProtocolBinary.MaxInteger
        && Material.PasswordWrapper.KeyEpoch == current.PasswordWrapper.KeyEpoch + 1
        && Material.RecoveryWrapper.Generation == current.RecoveryWrapper.Generation
        && Material.UnlockVerifier == current.UnlockVerifier && Material.RecoveryVerifier == current.RecoveryVerifier
        && Material.RecipientKey == current.RecipientKey && Material.AuthorKey == current.AuthorKey
        && Material.PasswordWrapper.KeySalt != current.PasswordWrapper.KeySalt
        && Material.PasswordWrapper.Nonce != current.PasswordWrapper.Nonce
        && Material.PasswordWrapper.Kdf.Salt != current.PasswordWrapper.Kdf.Salt
        && Material.RecoveryWrapper.KeySalt != current.RecoveryWrapper.KeySalt
        && Material.RecoveryWrapper.Nonce != current.RecoveryWrapper.Nonce;
}

public sealed record ProtectionChangeRequest(Guid Actor, string AccessVerifier, Guid ChallengeId, string Proof, byte[] RawBody, ProtectionChange Change);
public sealed record ProtectionChangeDetails(Guid AccountId, long ProtectionRevision, long KeyEpoch, long RecoveryGeneration, long AccountRevision);
public interface IVaultProtectionChangeStore
{
    Task<VaultResult<ProtectionChangeDetails>> ChangeAsync(ProtectionChangeRequest request, CancellationToken cancellationToken);
}
