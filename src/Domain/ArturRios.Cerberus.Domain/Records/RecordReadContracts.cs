using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Records;

public sealed record RecordReadRequest(Guid Actor, string AccessVerifier, Guid RecordId);
public sealed record RecordReadDetails(RecordListRow Record, Guid[] ProfileIds, Guid? FolderId, Guid[] CollectionIds);
public interface IRecordReadStore
{
    Task<VaultResult<RecordReadDetails>> ReadAsync(RecordReadRequest request, CancellationToken cancellationToken);
}
