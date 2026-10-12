using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Collections;

public sealed class ChangeCollectionAccessOutput : CommandOutput
{
    public Guid CollectionId { get; set; }
    public Guid GrantId { get; set; }
    public long Revision { get; set; }
    public string Access { get; set; } = null!;
    public Guid[] RecordIds { get; set; } = [];
    public Guid[] FolderIds { get; set; } = [];
}
