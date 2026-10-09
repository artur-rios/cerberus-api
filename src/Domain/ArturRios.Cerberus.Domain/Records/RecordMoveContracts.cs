using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Records;

public sealed record RecordMoveRequest(Guid Actor,string AccessVerifier,Guid RecordId,long ExpectedRevision,Guid? FolderId);
public sealed record RecordMoveDetails(Guid RecordId,Guid? FolderId,long Revision,long ServerSequence);
public interface IRecordMoveStore
{
    Task<VaultResult<RecordMoveDetails>> MoveAsync(RecordMoveRequest request,CancellationToken cancellationToken);
}
