using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Folders;

public sealed record FolderReadRequest(Guid Actor, string AccessVerifier, Guid FolderId);
public sealed record FolderReadDetails(FolderListRow Folder, Guid[] ProfileIds, Guid? ParentFolderId, Guid[] CollectionIds);
public interface IFolderReadStore
{
    Task<VaultResult<FolderReadDetails>> ReadAsync(FolderReadRequest request, CancellationToken cancellationToken);
}
