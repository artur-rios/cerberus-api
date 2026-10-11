using ArturRios.Mediator.Query;

namespace ArturRios.Cerberus.Query.Collections;

public sealed class GetCollectionQuery(Guid actor, string? access, Guid collectionId) : BaseQuery
{
    public Guid Actor { get; } = actor;
    public string? Access { get; } = access;
    public Guid CollectionId { get; } = collectionId;
}
