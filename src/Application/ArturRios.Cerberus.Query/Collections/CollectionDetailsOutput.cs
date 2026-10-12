using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Mediator.Query;

namespace ArturRios.Cerberus.Query.Collections;

public sealed class CollectionDetailsOutput : QueryOutput
{
    public Guid CollectionId { get; init; }
    public long Revision { get; init; }
    public long ServerSequence { get; init; }
    public DateTimeOffset EditedAt { get; init; }
    public required EncryptedEnvelope Envelope { get; init; }
    public Guid[] ProfileIds { get; init; } = [];
    public Guid[] RecordIds { get; init; } = [];
    public Guid[] FolderIds { get; init; } = [];
}
