using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;

namespace ArturRios.Cerberus.Command.Sync;

public sealed class UploadSyncEditsHandler(ISyncEditsStore store, CerberusOptions options)
    : ICommandHandlerAsync<UploadSyncEditsCommand, SyncEditsOutput>
{
    public async Task<DataOutput<SyncEditsOutput?>> HandleAsync(UploadSyncEditsCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<SyncEditsOutput?>.New;
        if (command.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (command.Access is null) return output.WithError("vault_access_required");
        var input = new SyncEditsInput(command.Edits);
        if (!OpaqueAccessHandle.TryHash(command.Access, out var verifier) || !input.IsValid(options.MaxPageSize))
            return output.WithError("validation_failed");
        var result = await store.UploadAsync(new(command.Actor, verifier, input), cancellationToken);
        if (result.Error is not null) return output.WithError(result.Details is not null || !SyncEditsMessages.StatusCodes.ContainsKey(result.Error)
            ? "persistence_unavailable" : result.Error);
        var data = result.Details;
        if (data is null || data.AccountId == Guid.Empty || data.ProfileId == Guid.Empty || !SyncResourceData.ValidTime(data.ServerTime)
            || data.Outcomes is null || data.Outcomes.Count != input.Edits.Length) return output.WithError("persistence_unavailable");
        for (var i = 0; i < input.Edits.Length; i++)
        {
            var edit = input.Edits[i]; var item = data.Outcomes[i];
            if (item?.IsValid() != true || item.OperationId != edit.OperationId || item.ResourceKind != edit.ResourceKind || item.ResourceId != edit.ResourceId
                || item.Status == "applied" && item.EditedAt != OfflineEditOrder.Normalize(edit.EditedAt)
                || item.Status == "superseded" && edit.Action == "update" && item.EditedAt < OfflineEditOrder.Normalize(edit.EditedAt))
                return output.WithError("persistence_unavailable");
        }
        return output.WithData(new SyncEditsOutput { AccountId = data.AccountId, ProfileId = data.ProfileId,
            ServerTime = data.ServerTime, Outcomes = data.Outcomes }).WithMessage("sync_edits_processed");
    }
}
