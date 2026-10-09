using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
namespace ArturRios.Cerberus.Query.Records;

public sealed class GetRecordHandler(IRecordReadStore store) : IQueryHandlerAsync<GetRecordQuery, RecordDetailsOutput>
{
    public async Task<DataOutput<RecordDetailsOutput?>> HandleAsync(GetRecordQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<RecordDetailsOutput?>.New;
        if (query.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (query.Access is null) return output.WithError("vault_access_required");
        if (query.RecordId == Guid.Empty || !OpaqueAccessHandle.TryHash(query.Access, out var verifier))
            return output.WithError("validation_failed");
        var result = await store.ReadAsync(new(query.Actor, verifier, query.RecordId), cancellationToken);
        if (result?.Error is not null)
            return output.WithError(result.Data is not null ? "persistence_unavailable" : RecordReadMessages.SafeError(result.Error));
        var data = result?.Data;
        if (data?.Record is null || data.Record.RecordId != query.RecordId
            || !ValidIds(data.ProfileIds) || !ValidIds(data.CollectionIds) || data.FolderId == Guid.Empty
            || !RecordProjection.TryRead(data.Record, out var record))
            return output.WithError("persistence_unavailable");
        return output.WithData(new RecordDetailsOutput
        {
            RecordId = record!.RecordId, Revision = record.Revision, ServerSequence = record.ServerSequence,
            EditedAt = record.EditedAt, Envelope = record.Envelope,
            ProfileIds = data.ProfileIds.Order().ToArray(), FolderId = data.FolderId, CollectionIds = data.CollectionIds.Order().ToArray()
        }).WithMessage("record_found");
    }
    private static bool ValidIds(Guid[]? ids) => ids is not null && ids.All(x => x != Guid.Empty) && ids.Distinct().Count() == ids.Length;
}
