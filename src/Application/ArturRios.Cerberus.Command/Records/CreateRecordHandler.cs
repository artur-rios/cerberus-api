using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Records;

public sealed class CreateRecordHandler(IValidator<CreateRecordCommand> validator, IRecordCreateStore store)
    : ICommandHandlerAsync<CreateRecordCommand, CreateRecordOutput>
{
    public async Task<DataOutput<CreateRecordOutput?>> HandleAsync(CreateRecordCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<CreateRecordOutput?>.New;
        if (command.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (command.VaultAccess is null) return output.WithError("vault_access_required");
        if (!OpaqueAccessHandle.TryHash(command.VaultAccess, out var verifier)
            || !(await validator.ValidateAsync(command, cancellationToken)).IsValid) return output.WithError("validation_failed");
        var result = await store.CreateAsync(new(command.Actor, verifier, command.ToInput()), cancellationToken);
        if (result?.Error is not null) return output.WithError(RecordCreateMessages.SafeError(result.Error));
        var data = result?.Data;
        var expectedTime = command.EditedAt.AddTicks(-(command.EditedAt.Ticks % TimeSpan.TicksPerMicrosecond));
        if (data is null || data.RecordId != command.RecordId || data.Revision != 1
            || data.ServerSequence is <= 0 or > ProtocolBinary.MaxInteger || data.EditedAt.Offset != TimeSpan.Zero
            || data.EditedAt.Ticks < TimeSpan.TicksPerMicrosecond || data.EditedAt.Ticks % TimeSpan.TicksPerMicrosecond != 0
            || data.EditedAt != expectedTime) return output.WithError("persistence_unavailable");
        return output.WithData(new CreateRecordOutput { RecordId = data.RecordId, Revision = data.Revision,
            ServerSequence = data.ServerSequence, EditedAt = data.EditedAt }).WithMessage("record_created");
    }
}
