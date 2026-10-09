using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Collections;

public sealed class CreateCollectionOutput : CommandOutput
{
    public Guid CollectionId { get; set; }
    public long Revision { get; set; }
    public long ServerSequence { get; set; }
    public DateTimeOffset EditedAt { get; set; }
}
