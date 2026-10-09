using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Records;

public sealed record RecordTrashRequest(Guid Actor,string AccessVerifier,Guid RecordId,long ExpectedRevision);
public sealed record RecordTrashDetails(Guid RecordId,Guid TrashOperationId,long Revision,long ServerSequence,
    DateTimeOffset DeletedAt,DateTimeOffset PurgeAt);
public interface IRecordTrashStore
{
    Task<VaultResult<RecordTrashDetails>> TrashAsync(RecordTrashRequest request,CancellationToken cancellationToken);
}
