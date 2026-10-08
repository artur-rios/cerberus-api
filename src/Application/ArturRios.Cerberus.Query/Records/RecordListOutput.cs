using ArturRios.Cerberus.Domain.Accounts;
namespace ArturRios.Cerberus.Query.Records;

public sealed record RecordListItem(Guid RecordId, long Revision, long ServerSequence, DateTimeOffset EditedAt, EncryptedEnvelope Envelope);
public sealed class RecordListOutput(IReadOnlyList<RecordListItem> items, string? nextCursor) : ArturRios.Mediator.Query.QueryOutput
{
    public IReadOnlyList<RecordListItem> Items { get; } = items;
    public string? NextCursor { get; } = nextCursor;
}
