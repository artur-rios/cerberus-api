using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Collections;

public sealed record CollectionTrashRequest(Guid Actor, string AccessVerifier, Guid CollectionId, long ExpectedRevision);

public sealed record CollectionTrashDetails(Guid CollectionId, Guid TrashOperationId, long Revision, long ServerSequence,
    DateTimeOffset DeletedAt, DateTimeOffset PurgeAt);

public interface ICollectionTrashStore
{
    Task<VaultResult<CollectionTrashDetails>> TrashAsync(CollectionTrashRequest request, CancellationToken cancellationToken);
}
