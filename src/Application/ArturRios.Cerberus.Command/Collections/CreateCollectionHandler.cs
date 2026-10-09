using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Collections;

public sealed class CreateCollectionHandler(IValidator<CreateCollectionCommand> validator, ICollectionCreateStore store)
    : ICommandHandlerAsync<CreateCollectionCommand, CreateCollectionOutput>
{
    public async Task<DataOutput<CreateCollectionOutput?>> HandleAsync(CreateCollectionCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<CreateCollectionOutput?>.New;
        if (command.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (command.VaultAccess is null) return output.WithError("vault_access_required");
        if (!OpaqueAccessHandle.TryHash(command.VaultAccess, out var verifier)
            || !(await validator.ValidateAsync(command, cancellationToken)).IsValid) return output.WithError("validation_failed");
        var result = await store.CreateAsync(new(command.Actor, verifier, command.ToInput()), cancellationToken);
        if (result?.Error is not null) return output.WithError(CreateCollectionMessages.SafeError(result.Error));
        var data = result?.Data;
        var expectedTime = command.EditedAt.AddTicks(-(command.EditedAt.Ticks % TimeSpan.TicksPerMicrosecond));
        if (data is null || data.CollectionId != command.CollectionId || data.Revision != 1
            || data.ServerSequence is <= 0 or > ProtocolBinary.MaxInteger || data.EditedAt.Offset != TimeSpan.Zero
            || data.EditedAt.Ticks < TimeSpan.TicksPerMicrosecond || data.EditedAt.Ticks % TimeSpan.TicksPerMicrosecond != 0
            || data.EditedAt != expectedTime) return output.WithError("persistence_unavailable");
        return output.WithData(new CreateCollectionOutput { CollectionId = data.CollectionId, Revision = data.Revision,
            ServerSequence = data.ServerSequence, EditedAt = data.EditedAt }).WithMessage("collection_created");
    }
}
