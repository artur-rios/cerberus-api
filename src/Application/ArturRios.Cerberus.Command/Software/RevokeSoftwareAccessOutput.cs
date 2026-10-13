using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Software;

public sealed class RevokeSoftwareAccessOutput : CommandOutput
{
    public Guid GrantId { get; set; }
    public long Revision { get; set; }
}
