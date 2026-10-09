using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Mediator.Query;
namespace ArturRios.Cerberus.Query.Folders;

public sealed class FolderDetailsOutput : QueryOutput
{
    public Guid FolderId { get; init; }
    public long Revision { get; init; }
    public long ServerSequence { get; init; }
    public DateTimeOffset EditedAt { get; init; }
    public required EncryptedEnvelope Envelope { get; init; }
    public Guid[] ProfileIds { get; init; } = [];
    public Guid? ParentFolderId { get; init; }
    public Guid[] CollectionIds { get; init; } = [];
}
