using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Folders;

public sealed record FolderListRequest(Guid Actor, string AccessVerifier, int PageSize, long After, long? Boundary);
public sealed record FolderListRow(Guid FolderId, long Revision, long ServerSequence, DateTimeOffset EditedAt, byte[] Envelope);
public sealed record FolderListPage(IReadOnlyList<FolderListRow> Items, long Boundary, bool HasMore);
public interface IFolderListStore
{
    Task<VaultResult<FolderListPage>> ListAsync(FolderListRequest request, CancellationToken cancellationToken);
}
