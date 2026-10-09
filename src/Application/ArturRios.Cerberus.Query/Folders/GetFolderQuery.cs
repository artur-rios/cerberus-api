using ArturRios.Mediator.Query;
namespace ArturRios.Cerberus.Query.Folders;

public sealed class GetFolderQuery(Guid actor, string? access, Guid recordId) : BaseQuery
{
    public Guid Actor { get; } = actor;
    public string? Access { get; } = access;
    public Guid FolderId { get; } = recordId;
}
