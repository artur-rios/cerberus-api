using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Collections;

public sealed record CollectionListRequest(Guid Actor, string AccessVerifier, int PageSize, long After, long? Boundary);
public sealed record CollectionListRow(Guid CollectionId, long Revision, long ServerSequence, DateTimeOffset EditedAt, byte[] Envelope, long KeyEpoch);
public sealed record CollectionListPage(IReadOnlyList<CollectionListRow> Items, long Boundary, bool HasMore);
public interface ICollectionListStore
{
    Task<VaultResult<CollectionListPage>> ListAsync(CollectionListRequest request, CancellationToken cancellationToken);
}
