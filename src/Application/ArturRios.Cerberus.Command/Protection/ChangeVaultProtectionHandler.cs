using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Protection;

public sealed class ChangeVaultProtectionHandler(IValidator<ChangeVaultProtectionCommand> validator, IVaultProtectionChangeStore store)
    : ICommandHandlerAsync<ChangeVaultProtectionCommand, ChangeVaultProtectionOutput>
{
    public async Task<DataOutput<ChangeVaultProtectionOutput?>> HandleAsync(ChangeVaultProtectionCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<ChangeVaultProtectionOutput?>.New;
        if (command.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (command.Access is null) return output.WithError("vault_access_required");
        if (!OpaqueAccessHandle.TryHash(command.Access, out var verifier)) return output.WithError("validation_failed");
        if (!(await validator.ValidateAsync(command, cancellationToken)).IsValid) return output.WithError("validation_failed");
        var result = await store.ChangeAsync(new(command.Actor, verifier, command.ChallengeId, command.Proof, command.RawBody, command.ToChange()), cancellationToken);
        if (result.Error is not null) return output.WithError(result.Error);
        var data = result.Data;
        if (data is null || data.AccountId != command.AccountId || data.ProtectionRevision != command.ExpectedProtectionRevision + 1
            || data.KeyEpoch != command.Material.PasswordWrapper.KeyEpoch || data.RecoveryGeneration != command.Material.RecoveryWrapper.Generation
            || data.AccountRevision != command.ExpectedAccountRevision + (command.Mode == "rotate-content" ? 1 : 0)
            || data.ProtectionRevision > ProtocolBinary.MaxInteger || data.AccountRevision > ProtocolBinary.MaxInteger)
            return output.WithError("persistence_unavailable");
        return output.WithData(new ChangeVaultProtectionOutput { AccountId = data.AccountId, ProtectionRevision = data.ProtectionRevision,
            KeyEpoch = data.KeyEpoch, RecoveryGeneration = data.RecoveryGeneration, AccountRevision = data.AccountRevision }).WithMessage("protection_changed");
    }
}
