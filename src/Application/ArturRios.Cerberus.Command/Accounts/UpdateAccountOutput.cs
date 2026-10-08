using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Accounts;

public sealed class UpdateAccountOutput : CommandOutput
{
    public Guid Id { get; set; }
    public long Revision { get; set; }
}
