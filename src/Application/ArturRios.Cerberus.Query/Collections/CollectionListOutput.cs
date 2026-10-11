using ArturRios.Cerberus.Domain.Accounts;
namespace ArturRios.Cerberus.Query.Collections;

public sealed record CollectionListItem(Guid CollectionId, long Revision, long ServerSequence, DateTimeOffset EditedAt, EncryptedEnvelope Envelope);
public sealed class CollectionListOutput(IReadOnlyList<CollectionListItem> items, string? nextCursor) : ArturRios.Mediator.Query.QueryOutput
{
    public IReadOnlyList<CollectionListItem> Items { get; } = items;
    public string? NextCursor { get; } = nextCursor;
}
