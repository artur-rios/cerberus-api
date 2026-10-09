using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Folders;

public sealed class CreateFolderHandler(IValidator<CreateFolderCommand> validator, IFolderCreateStore store)
    : ICommandHandlerAsync<CreateFolderCommand, CreateFolderOutput>
{
    public async Task<DataOutput<CreateFolderOutput?>> HandleAsync(CreateFolderCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<CreateFolderOutput?>.New;
        if (command.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (command.VaultAccess is null) return output.WithError("vault_access_required");
        if (!OpaqueAccessHandle.TryHash(command.VaultAccess, out var verifier)
            || !(await validator.ValidateAsync(command, cancellationToken)).IsValid) return output.WithError("validation_failed");
        var result = await store.CreateAsync(new(command.Actor, verifier, command.ToInput()), cancellationToken);
        if (result?.Error is not null) return output.WithError(FolderCreateMessages.SafeError(result.Error));
        var data = result?.Data;
        var expectedTime = command.EditedAt.AddTicks(-(command.EditedAt.Ticks % TimeSpan.TicksPerMicrosecond));
        if (data is null || data.FolderId != command.FolderId || data.Revision != 1
            || data.ServerSequence is <= 0 or > ProtocolBinary.MaxInteger || data.EditedAt.Offset != TimeSpan.Zero
            || data.EditedAt.Ticks < TimeSpan.TicksPerMicrosecond || data.EditedAt.Ticks % TimeSpan.TicksPerMicrosecond != 0
            || data.EditedAt != expectedTime) return output.WithError("persistence_unavailable");
        return output.WithData(new CreateFolderOutput { FolderId = data.FolderId, Revision = data.Revision,
            ServerSequence = data.ServerSequence, EditedAt = data.EditedAt }).WithMessage("folder_created");
    }
}
