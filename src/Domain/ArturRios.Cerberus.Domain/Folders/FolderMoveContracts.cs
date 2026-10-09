using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Folders;

public sealed record FolderMoveRequest(Guid Actor,string AccessVerifier,Guid FolderId,long ExpectedRevision,Guid? ParentFolderId);
public sealed record FolderMoveDetails(Guid FolderId,Guid? ParentFolderId,long Revision,long ServerSequence);
public interface IFolderMoveStore
{
    Task<VaultResult<FolderMoveDetails>> MoveAsync(FolderMoveRequest request,CancellationToken cancellationToken);
}
