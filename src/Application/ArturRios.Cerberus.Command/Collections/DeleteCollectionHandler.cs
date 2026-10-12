using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Collections;

public sealed class DeleteCollectionHandler(IValidator<DeleteCollectionCommand> validator, ICollectionTrashStore store)
    : ICommandHandlerAsync<DeleteCollectionCommand, DeleteCollectionOutput>
{
    public async Task<DataOutput<DeleteCollectionOutput?>> HandleAsync(DeleteCollectionCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<DeleteCollectionOutput?>.New;
        if (command.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (command.VaultAccess is null) return output.WithError("vault_access_required");
        if (!OpaqueAccessHandle.TryHash(command.VaultAccess, out var verifier)
            || !(await validator.ValidateAsync(command, cancellationToken)).IsValid)
            return output.WithError("validation_failed");

        var result = await store.TrashAsync(new(command.Actor, verifier, command.CollectionId, command.ExpectedRevision),
            cancellationToken);
        if (result is null || result.Error is not null && result.Data is not null)
            return output.WithError("persistence_unavailable");
        if (result.Error is not null) return output.WithError(DeleteCollectionMessages.SafeError(result.Error));

        var data = result.Data;
        if (data is null || data.CollectionId != command.CollectionId || data.TrashOperationId == Guid.Empty
            || data.Revision != command.ExpectedRevision + 1 || data.Revision is <= 0 or > ProtocolBinary.MaxInteger
            || data.ServerSequence is <= 0 or > ProtocolBinary.MaxInteger
            || data.DeletedAt.Offset != TimeSpan.Zero || data.DeletedAt.Ticks < TimeSpan.TicksPerMicrosecond
            || data.DeletedAt.Ticks % TimeSpan.TicksPerMicrosecond != 0 || data.PurgeAt.Offset != TimeSpan.Zero
            || data.PurgeAt.Ticks % TimeSpan.TicksPerMicrosecond != 0
            || data.PurgeAt - data.DeletedAt != TimeSpan.FromDays(30))
            return output.WithError("persistence_unavailable");

        return output.WithData(new DeleteCollectionOutput
        {
            CollectionId = data.CollectionId,
            TrashOperationId = data.TrashOperationId,
            Revision = data.Revision,
            ServerSequence = data.ServerSequence,
            DeletedAt = data.DeletedAt,
            PurgeAt = data.PurgeAt
        }).WithMessage("collection_deleted");
    }
}
