using ArturRios.Mediator.Query;

namespace ArturRios.Cerberus.Query.Sync;

public sealed class DownloadSyncChangesQuery(Guid actor, string? access, string? cursor, int? pageSize) : BaseQuery
{
    public Guid Actor { get; } = actor;
    public string? Access { get; } = access;
    public string? Cursor { get; } = cursor;
    public int? RequestedPageSize { get; } = pageSize;
}
