using ArturRios.Cerberus.Domain.Accounts;
namespace ArturRios.Cerberus.Query.Folders;

public sealed record FolderListItem(Guid FolderId, long Revision, long ServerSequence, DateTimeOffset EditedAt, EncryptedEnvelope Envelope);
public sealed class FolderListOutput(IReadOnlyList<FolderListItem> items, string? nextCursor) : ArturRios.Mediator.Query.QueryOutput
{
    public IReadOnlyList<FolderListItem> Items { get; } = items;
    public string? NextCursor { get; } = nextCursor;
}
