using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Cerberus.Domain.Protection;

public sealed record VaultResult<T>(T? Data = null, string? Error = null) where T : class;
public sealed record VaultProtectionDetails(Guid AccountId, long ProtectionRevision, long KeyEpoch, long RecoveryGeneration, ProtectionMaterial Material);
public sealed record VaultAccessDetails(Guid AccountId, string Access, DateTimeOffset IssuedAt, DateTimeOffset? ExpiresAt);

public interface IVaultProtectionStore
{
    Task<VaultResult<VaultProtectionDetails>> InitializeAsync(Guid actor, Guid accountId, long expectedAccountRevision, ProtectionMaterial material, CancellationToken cancellationToken);
    Task<VaultResult<VaultProtectionDetails>> ReadAsync(Guid actor, CancellationToken cancellationToken);
    Task<VaultResult<VaultProofChallenge>> ChallengeAsync(Guid actor, string requestHash, CancellationToken cancellationToken);
    Task<VaultResult<VaultProofChallenge>> ChallengeAsync(Guid actor, string operation, string requestHash, CancellationToken cancellationToken);
    Task<VaultResult<VaultAccessDetails>> UnlockAsync(Guid actor, long expectedProtectionRevision, Guid challengeId, string proof, byte[] rawBody, CancellationToken cancellationToken);
}

public sealed class VaultProtection : Entity
{
    public long AccountId { get; set; }
    public long Revision { get; set; } = 1;
    public long KeyEpoch { get; set; } = 1;
    public long RecoveryGeneration { get; set; } = 1;
    public byte[] Material { get; set; } = [];
}

public sealed class VaultUnlockChallenge : Entity
{
    public long AccountId { get; set; }
    public Guid PublicId { get; set; }
    public byte[] Challenge { get; set; } = [];
    public long PolicyRevision { get; set; }
    public long RevocationGeneration { get; set; }
    public bool Consumed { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
