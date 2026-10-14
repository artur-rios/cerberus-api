using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;

namespace ArturRios.Cerberus.Query.Sync;

public sealed class DownloadSyncChangesHandler(ISyncDownloadStore store, CerberusOptions options, SyncCursor cursors)
    : IQueryHandlerAsync<DownloadSyncChangesQuery, SyncChangesOutput>
{
    public async Task<DataOutput<SyncChangesOutput?>> HandleAsync(DownloadSyncChangesQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<SyncChangesOutput?>.New;
        if (query.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (query.Access is null) return output.WithError("vault_access_required");
        if (!OpaqueAccessHandle.TryHash(query.Access, out var verifier)) return output.WithError("validation_failed");
        var size = query.RequestedPageSize ?? Math.Min(50, options.MaxPageSize);
        if (size <= 0 || size > options.MaxPageSize) return output.WithError("validation_failed");
        SyncCheckpoint? checkpoint = null;
        var invalid = false;
        if (query.Cursor is not null)
        {
            invalid = !cursors.TryDecode(query.Cursor, out var c) || c!.Actor != query.Actor || c.PageSize != size;
            if (!invalid) checkpoint = new(c!.ProfileId, c.StreamId, c.WindowVersion, c.After, c.Boundary);
        }
        var result = await store.DownloadAsync(new(query.Actor, verifier, size, checkpoint, invalid), cancellationToken);
        if (result.Error is not null) return output.WithError(result.Page is not null || !SyncDownloadMessages.StatusCodes.ContainsKey(result.Error)
            ? "persistence_unavailable" : result.Error);
        var p = result.Page;
        if (p is null || p.AccountId == Guid.Empty || p.ProfileId == Guid.Empty || !SyncResourceData.Safe(p.PolicyRevision)
            || !SyncResourceData.Safe(p.RevocationGeneration) || !SyncResourceData.ValidTime(p.ServerTime) || p.Items is null || p.Items.Count > size)
            return output.WithError("persistence_unavailable");
        string? next = null;
        if (p.ResynchronizationRequired)
        {
            if (p.Checkpoint is not null || p.Items.Count != 0 || p.HasMore || !p.ClearScope) return output.WithError("persistence_unavailable");
        }
        else
        {
            var cp = p.Checkpoint;
            if (cp?.IsValid() != true || cp.ProfileId != p.ProfileId || p.HasMore && (p.Items.Count != size || cp.After >= cp.Boundary))
                return output.WithError("persistence_unavailable");
            if (p.ClearScope != (checkpoint is null) || invalid || checkpoint is not null
                && (cp.ProfileId != checkpoint.ProfileId || cp.StreamId != checkpoint.StreamId
                    || cp.WindowVersion != checkpoint.WindowVersion && cp.WindowVersion != checkpoint.WindowVersion + 1
                    || cp.WindowVersion == checkpoint.WindowVersion && cp.Boundary != checkpoint.Boundary
                    || cp.WindowVersion != checkpoint.WindowVersion
                        && (checkpoint.After != checkpoint.Boundary || cp.Boundary <= checkpoint.Boundary)))
                return output.WithError("persistence_unavailable");
            var previous = checkpoint is null ? 0 : cp.WindowVersion == checkpoint.WindowVersion ? checkpoint.After : checkpoint.Boundary;
            var seen = new HashSet<(string, Guid)>(); var sawUpsert = false;
            foreach (var item in p.Items)
            {
                if (item?.IsValid(query.Actor, p.ProfileId) != true || item.Sequence <= previous || item.Sequence > cp.Boundary
                    || !seen.Add((item.ResourceKind, item.ResourceId)) || sawUpsert && item.EventType != "upsert")
                    return output.WithError("persistence_unavailable");
                previous = item.Sequence; sawUpsert |= item.EventType == "upsert";
            }
            if (previous != cp.After || !p.HasMore && cp.After != cp.Boundary) return output.WithError("persistence_unavailable");
            next = cursors.Encode(new(query.Actor, p.ProfileId, cp.StreamId, cp.WindowVersion, cp.After, cp.Boundary, size));
        }
        output.WithData(new SyncChangesOutput
        {
            AccountId = p.AccountId, ProfileId = p.ProfileId, PolicyRevision = p.PolicyRevision, RevocationGeneration = p.RevocationGeneration,
            ServerTime = p.ServerTime, Items = p.Items, NextCursor = next, HasMore = p.HasMore, ClearScope = p.ClearScope,
            ResynchronizationRequired = p.ResynchronizationRequired
        });
        return p.ResynchronizationRequired ? output.WithError("resynchronization_required") : output.WithMessage("sync_changes_found");
    }
}
