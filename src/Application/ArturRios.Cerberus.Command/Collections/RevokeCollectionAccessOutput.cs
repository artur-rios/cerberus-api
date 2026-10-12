using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Collections;

public sealed class RevokeCollectionAccessOutput : CommandOutput
{
    public Guid CollectionId { get; set; }
    public Guid GrantId { get; set; }
    public long Revision { get; set; }
}
