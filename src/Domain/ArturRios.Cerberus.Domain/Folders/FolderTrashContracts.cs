using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Folders;

public sealed record FolderTrashRequest(Guid Actor,string AccessVerifier,Guid FolderId,long ExpectedRevision);
public sealed record FolderTrashDetails(Guid FolderId,Guid TrashOperationId,long Revision,long ServerSequence,
    DateTimeOffset DeletedAt,DateTimeOffset PurgeAt);
public interface IFolderTrashStore
{
    Task<VaultResult<FolderTrashDetails>> TrashAsync(FolderTrashRequest request,CancellationToken cancellationToken);
}
