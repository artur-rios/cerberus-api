using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Records;

public sealed record RecordListRequest(Guid Actor, string AccessVerifier, int PageSize, long After, long? Boundary);
public sealed record RecordListRow(Guid RecordId, long Revision, long ServerSequence, DateTimeOffset EditedAt, byte[] Envelope);
public sealed record RecordListPage(IReadOnlyList<RecordListRow> Items, long Boundary, bool HasMore);
public interface IRecordListStore
{
    Task<VaultResult<RecordListPage>> ListAsync(RecordListRequest request, CancellationToken cancellationToken);
}
