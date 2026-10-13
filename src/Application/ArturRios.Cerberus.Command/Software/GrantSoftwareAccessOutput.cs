using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Software;

public sealed class GrantSoftwareAccessOutput : CommandOutput
{
    public Guid GrantId { get; set; }
    public long Revision { get; set; }
}
