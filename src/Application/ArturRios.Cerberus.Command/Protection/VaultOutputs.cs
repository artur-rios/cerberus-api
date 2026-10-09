using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Protection;

public sealed class VaultInitializationOutput : CommandOutput
{
    public Guid AccountId { get; init; }
    public long ProtectionRevision { get; init; }
    public long KeyEpoch { get; init; }
    public long RecoveryGeneration { get; init; }
}
public sealed class VaultChallengeOutput : CommandOutput
{
    public required VaultProofChallenge Challenge { get; init; }
}
public sealed class VaultUnlockOutput : CommandOutput
{
    public Guid AccountId { get; init; }
    public required string VaultAccess { get; init; }
    public DateTimeOffset IssuedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}
