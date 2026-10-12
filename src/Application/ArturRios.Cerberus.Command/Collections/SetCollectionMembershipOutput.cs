using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Collections;

public sealed class SetCollectionMembershipOutput : CommandOutput
{
    public Guid CollectionId { get; set; }
    public long Revision { get; set; }
    public long ServerSequence { get; set; }
    public DateTimeOffset EditedAt { get; set; }
    public Guid[] RecordIds { get; set; } = [];
    public Guid[] FolderIds { get; set; } = [];
}
