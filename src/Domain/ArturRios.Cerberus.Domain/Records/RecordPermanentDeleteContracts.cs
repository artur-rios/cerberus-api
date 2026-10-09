using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Records;

public sealed record RecordPermanentDeleteRequest(Guid Actor, string AccessVerifier, Guid RecordId, long ExpectedRevision);
public sealed record RecordPermanentDeleteDetails(Guid RecordId, DateTimeOffset DeletedAt);
public interface IRecordPermanentDeleteStore
{
    Task<VaultResult<RecordPermanentDeleteDetails>> DeleteAsync(RecordPermanentDeleteRequest request, CancellationToken cancellationToken);
}
