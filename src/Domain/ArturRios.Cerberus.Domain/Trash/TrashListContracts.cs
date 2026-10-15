using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Trash;

public sealed record TrashListRequest(Guid Actor, string AccessVerifier, int PageSize, long After, long? Boundary);
public sealed record TrashListRow(long Position, Guid OperationId, string Kind, Guid ResourceId, long Revision,
    long ServerSequence, DateTimeOffset EditedAt, DateTimeOffset DeletedAt, DateTimeOffset PurgeAt,
    byte[] Envelope, long? KeyEpoch = null, byte[]? KeyWrappers = null);
public sealed record TrashListPage(IReadOnlyList<TrashListRow> Items, long Boundary, bool HasMore);
public interface ITrashListStore
{
    Task<VaultResult<TrashListPage>> ListAsync(TrashListRequest request, CancellationToken cancellationToken);
}
