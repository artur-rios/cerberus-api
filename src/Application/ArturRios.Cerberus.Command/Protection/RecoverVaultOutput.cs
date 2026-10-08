using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Protection;

public sealed class RecoverVaultOutput : CommandOutput
{
    public string Status { get; init; } = null!;
    public long ProtectionRevision { get; init; }
    public long Generation { get; init; }
    public long RevocationGeneration { get; init; }
}
