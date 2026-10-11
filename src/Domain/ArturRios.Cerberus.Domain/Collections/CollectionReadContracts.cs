using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Collections;

public sealed record CollectionReadRequest(Guid Actor, string AccessVerifier, Guid CollectionId);
public sealed record CollectionReadDetails(CollectionListRow Collection, Guid[] ProfileIds, Guid[] RecordIds, Guid[] FolderIds);
public interface ICollectionReadStore
{
    Task<VaultResult<CollectionReadDetails>> ReadAsync(CollectionReadRequest request, CancellationToken cancellationToken);
}
