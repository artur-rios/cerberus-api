using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Accounts;

public sealed class AccountClosureOutput : CommandOutput
{
    public Guid AccountId { get; init; }
    public long Revision { get; init; }
    public long RevocationGeneration { get; init; }
    public DateTimeOffset ClosureRequestedAt { get; init; }
    public DateTimeOffset ClosureDeadline { get; init; }
    public DateTimeOffset ServerTime { get; init; }
    public bool AlreadyPending { get; init; }
}
