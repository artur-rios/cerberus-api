using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;

namespace ArturRios.Cerberus.Query.Collections;

public sealed class GetCollectionHandler(ICollectionReadStore store) : IQueryHandlerAsync<GetCollectionQuery, CollectionDetailsOutput>
{
    public async Task<DataOutput<CollectionDetailsOutput?>> HandleAsync(GetCollectionQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<CollectionDetailsOutput?>.New;
        if (query.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (query.Access is null) return output.WithError("vault_access_required");
        if (query.CollectionId == Guid.Empty || !OpaqueAccessHandle.TryHash(query.Access, out var verifier))
            return output.WithError("validation_failed");

        VaultResult<CollectionReadDetails>? result;
        try { result = await store.ReadAsync(new(query.Actor, verifier, query.CollectionId), cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return output.WithError("persistence_unavailable"); }

        if (result?.Error is not null)
            return output.WithError(result.Data is not null ? "persistence_unavailable" : CollectionReadMessages.SafeError(result.Error));
        var data = result?.Data;
        if (data?.Collection is null || data.Collection.CollectionId != query.CollectionId
            || !ValidIds(data.ProfileIds) || !ValidIds(data.RecordIds) || !ValidIds(data.FolderIds)
            || !CollectionProjection.TryRead(data.Collection, out var collection))
            return output.WithError("persistence_unavailable");

        return output.WithData(new CollectionDetailsOutput
        {
            CollectionId = collection!.CollectionId, Revision = collection.Revision, ServerSequence = collection.ServerSequence,
            EditedAt = collection.EditedAt, Envelope = collection.Envelope,
            ProfileIds = data.ProfileIds.Order().ToArray(), RecordIds = data.RecordIds.Order().ToArray(), FolderIds = data.FolderIds.Order().ToArray()
        }).WithMessage("collection_found");
    }

    private static bool ValidIds(Guid[]? ids) => ids is not null && ids.All(x => x != Guid.Empty) && ids.Distinct().Count() == ids.Length;
}
