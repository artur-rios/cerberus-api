using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Mediator.Query;
namespace ArturRios.Cerberus.Query.Records;

public sealed class RecordDetailsOutput : QueryOutput
{
    public Guid RecordId { get; init; }
    public long Revision { get; init; }
    public long ServerSequence { get; init; }
    public DateTimeOffset EditedAt { get; init; }
    public required EncryptedEnvelope Envelope { get; init; }
    public Guid[] ProfileIds { get; init; } = [];
    public Guid? FolderId { get; init; }
    public Guid[] CollectionIds { get; init; } = [];
}
