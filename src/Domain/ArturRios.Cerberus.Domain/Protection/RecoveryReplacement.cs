using System.Text.Json.Serialization;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Cerberus.Domain.Protection;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record RecoveryReplacement(string Operation, Guid IdempotencyKey, long ExpectedRevision,
    PasswordWrapper PasswordWrapper, RecoveryWrapper RecoveryWrapper, PublicJwk NewRecoveryVerifier)
{
    public bool IsValid() => Operation == "recover" && IdempotencyKey != Guid.Empty
        && ExpectedRevision is > 0 and <= ProtocolBinary.MaxInteger
        && PasswordWrapper?.IsValid() == true && RecoveryWrapper?.IsValid() == true && NewRecoveryVerifier?.IsValid() == true
        && PasswordWrapper.KeyEpoch == RecoveryWrapper.KeyEpoch && RecoveryWrapper.ProofKeyFingerprint == NewRecoveryVerifier.Fingerprint();

    public bool IsValidTransition(ProtectionMaterial current) => IsValid() && current?.IsValid() == true
        && current.PasswordWrapper.KeyEpoch < ProtocolBinary.MaxInteger && current.RecoveryWrapper.Generation < ProtocolBinary.MaxInteger
        && PasswordWrapper.KeyEpoch == current.PasswordWrapper.KeyEpoch + 1 && RecoveryWrapper.Generation == current.RecoveryWrapper.Generation + 1
        && NewRecoveryVerifier != current.RecoveryVerifier && NewRecoveryVerifier != current.UnlockVerifier
        && NewRecoveryVerifier != current.RecipientKey && NewRecoveryVerifier != current.AuthorKey
        && PasswordWrapper.KeySalt != current.PasswordWrapper.KeySalt && PasswordWrapper.Nonce != current.PasswordWrapper.Nonce
        && PasswordWrapper.Kdf.Salt != current.PasswordWrapper.Kdf.Salt
        && RecoveryWrapper.KeySalt != current.RecoveryWrapper.KeySalt && RecoveryWrapper.Nonce != current.RecoveryWrapper.Nonce;

    public ProtectionMaterial Replace(ProtectionMaterial current) => current with
    { PasswordWrapper = PasswordWrapper, RecoveryWrapper = RecoveryWrapper, RecoveryVerifier = NewRecoveryVerifier };
}

public sealed record RecoveryRequest(Guid Actor, long IdentityIssuedAt, Guid ChallengeId, string Proof, byte[] RawBody, RecoveryReplacement Replacement);
public sealed record RecoveryDetails(string Status, long ProtectionRevision, long Generation, long RevocationGeneration);
public interface IVaultRecoveryStore
{
    Task<VaultResult<RecoveryDetails>> RecoverAsync(RecoveryRequest request, CancellationToken cancellationToken);
}
public sealed class VaultRecoveryOperation : Entity
{
    public long AccountId { get; set; }
    public Guid IdempotencyKey { get; set; }
    public string RequestHash { get; set; } = null!;
    public long ProtectionRevision { get; set; }
    public long Generation { get; set; }
    public long RevocationGeneration { get; set; }
}
