using ArturRios.Mediator.Query;
namespace ArturRios.Cerberus.Query.Profiles;
public sealed class ListProfilesQuery(Guid actor, string? access, int? pageSize, string? cursor) : BaseQuery
{
    public Guid Actor { get; } = actor;
    public string? Access { get; } = access;
    public int? RequestedPageSize { get; } = pageSize;
    public string? Cursor { get; } = cursor;
}
