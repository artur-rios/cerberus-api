using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Protection;

public sealed class ChangeVaultProtectionOutput : CommandOutput
{
    public Guid AccountId { get; init; }
    public long ProtectionRevision { get; init; }
    public long KeyEpoch { get; init; }
    public long RecoveryGeneration { get; init; }
    public long AccountRevision { get; init; }
}
