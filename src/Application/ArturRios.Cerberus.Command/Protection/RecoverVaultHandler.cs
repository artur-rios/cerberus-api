using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Protection;

public sealed class RecoverVaultHandler(IValidator<RecoverVaultCommand> validator, IVaultRecoveryStore store)
    : ICommandHandlerAsync<RecoverVaultCommand, RecoverVaultOutput>
{
    public async Task<DataOutput<RecoverVaultOutput?>> HandleAsync(RecoverVaultCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<RecoverVaultOutput?>.New;
        if (command.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (!(await validator.ValidateAsync(command, cancellationToken)).IsValid) return output.WithError("validation_failed");
        var result = await store.RecoverAsync(new(command.Actor, command.IdentityIssuedAt, command.ChallengeId, command.Proof, command.RawBody, command.ToReplacement()), cancellationToken);
        if (result.Error is not null) return output.WithError(result.Error);
        var data = result.Data;
        if (data is null || data.Status != "committed" || data.ProtectionRevision != command.ExpectedRevision + 1
            || data.ProtectionRevision > ProtocolBinary.MaxInteger || data.Generation != command.RecoveryWrapper.Generation
            || data.RevocationGeneration is <= 0 or > ProtocolBinary.MaxInteger) return output.WithError("persistence_unavailable");
        return output.WithData(new RecoverVaultOutput { Status = data.Status, ProtectionRevision = data.ProtectionRevision,
            Generation = data.Generation, RevocationGeneration = data.RevocationGeneration }).WithMessage("recovery_committed");
    }
}
