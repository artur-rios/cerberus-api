using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Trash;

public sealed record TrashRestoreRequest(Guid Actor,string AccessVerifier,Guid OperationId,long ExpectedRevision);
public sealed record TrashRestoredItem(string Kind,Guid ResourceId,long Revision,long ServerSequence,DateTimeOffset EditedAt);
public sealed record TrashRestoreOmission(string ResourceKind,Guid ResourceId,string AssociationKind,int Count);
public sealed record TrashRestoreDetails(Guid OperationId,string RootKind,Guid RootResourceId,DateTimeOffset RestoredAt,
    IReadOnlyList<TrashRestoredItem> Items,IReadOnlyList<TrashRestoreOmission> Omissions);
public interface ITrashRestoreStore
{
    Task<VaultResult<TrashRestoreDetails>> RestoreAsync(TrashRestoreRequest request,CancellationToken cancellationToken);
}
