using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Collections;

public sealed class GrantCollectionAccessHandler(IValidator<GrantCollectionAccessCommand> validator, ICollectionGrantStore store)
    : ICommandHandlerAsync<GrantCollectionAccessCommand, GrantCollectionAccessOutput>
{
    public async Task<DataOutput<GrantCollectionAccessOutput?>> HandleAsync(GrantCollectionAccessCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<GrantCollectionAccessOutput?>.New;
        if (command.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (command.VaultAccess is null) return output.WithError("vault_access_required");
        if (!OpaqueAccessHandle.TryHash(command.VaultAccess, out var verifier)
            || !(await validator.ValidateAsync(command, cancellationToken)).IsValid)
            return output.WithError("validation_failed");

        var result = await store.GrantAsync(
            new(command.Actor, verifier, command.CollectionId, command.ToInput()), cancellationToken);
        if (result is null || result.Error is not null && result.Data is not null)
            return output.WithError("persistence_unavailable");
        if (result.Error is not null) return output.WithError(CollectionGrantMessages.SafeError(result.Error));

        var data = result.Data;
        if (data is null || data.CollectionId != command.CollectionId || data.GrantId != command.GrantId
            || data.Revision != (command.ExpectedRevision ?? 0) + 1
            || data.Revision is <= 0 or > ProtocolBinary.MaxInteger || data.Access != command.Access
            || !CollectionMembershipInput.ValidIds(data.RecordIds) || !CollectionMembershipInput.ValidIds(data.FolderIds))
            return output.WithError("persistence_unavailable");

        return output.WithData(new GrantCollectionAccessOutput
        {
            CollectionId = data.CollectionId,
            GrantId = data.GrantId,
            Revision = data.Revision,
            Access = data.Access,
            RecordIds = data.RecordIds.Order().ToArray(),
            FolderIds = data.FolderIds.Order().ToArray()
        }).WithMessage("collection_access_granted");
    }
}
