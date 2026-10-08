using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Protection;

public sealed class InitializeVaultHandler(IValidator<InitializeVaultCommand> validator, IVaultProtectionStore store)
    : ICommandHandlerAsync<InitializeVaultCommand, VaultInitializationOutput>
{
    public async Task<DataOutput<VaultInitializationOutput?>> HandleAsync(InitializeVaultCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var output = DataOutput<VaultInitializationOutput?>.New;
        if (command.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (!(await validator.ValidateAsync(command, cancellationToken)).IsValid) return output.WithError("validation_failed");
        var result = await store.InitializeAsync(command.Actor, command.AccountId, command.ExpectedAccountRevision, command.Material, cancellationToken);
        if (result.Error is not null) return output.WithError(result.Error);
        var data = result.Data;
        if (data is null || data.AccountId != command.AccountId || data.ProtectionRevision != 1 || data.KeyEpoch != 1 || data.RecoveryGeneration != 1)
            return output.WithError("persistence_unavailable");
        return output.WithData(new VaultInitializationOutput { AccountId = data.AccountId, ProtectionRevision = data.ProtectionRevision,
            KeyEpoch = data.KeyEpoch, RecoveryGeneration = data.RecoveryGeneration }).WithMessage("protection_initialized");
    }
}

public sealed class IssueVaultChallengeHandler(IValidator<IssueVaultChallengeCommand> validator, IVaultProtectionStore store)
    : ICommandHandlerAsync<IssueVaultChallengeCommand, VaultChallengeOutput>
{
    public async Task<DataOutput<VaultChallengeOutput?>> HandleAsync(IssueVaultChallengeCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var output = DataOutput<VaultChallengeOutput?>.New;
        if (command.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (!(await validator.ValidateAsync(command, cancellationToken)).IsValid) return output.WithError("validation_failed");
        var result = command.Operation == "unlock-account"
            ? await store.ChallengeAsync(command.Actor, command.RequestHash, cancellationToken)
            : await store.ChallengeAsync(command.Actor, command.Operation, command.RequestHash, cancellationToken);
        if (result.Error is not null) return output.WithError(result.Error);
        var c = result.Data;
        if (c is null || c.IdentityId != command.Actor || c.AccountId == Guid.Empty || c.ScopeId != c.AccountId
            || c.Format != "cerberus-challenge-v1" || c.Operation != command.Operation || c.ScopeKind != "account"
            || c.ChallengeId == Guid.Empty || (command.Operation == "recover" ? c.Generation is null or <= 0 or > ProtocolBinary.MaxInteger : c.Generation is not null) || c.RequestHash != command.RequestHash
            || c.KeyEpoch is <= 0 or > ProtocolBinary.MaxInteger || c.ProtectionRevision is <= 0 or > ProtocolBinary.MaxInteger
            || c.IssuedAt is < 0 or > ProtocolBinary.MaxInteger - 60 || c.ExpiresAt != c.IssuedAt + 60
            || !ProtocolBinary.TryDecode(c.Nonce, 32, out _)) return output.WithError("persistence_unavailable");
        return output.WithData(new VaultChallengeOutput { Challenge = c }).WithMessage("challenge_issued");
    }
}

public sealed class UnlockVaultHandler(IValidator<UnlockVaultCommand> validator, IVaultProtectionStore store)
    : ICommandHandlerAsync<UnlockVaultCommand, VaultUnlockOutput>
{
    public async Task<DataOutput<VaultUnlockOutput?>> HandleAsync(UnlockVaultCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var output = DataOutput<VaultUnlockOutput?>.New;
        if (command.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (!(await validator.ValidateAsync(command, cancellationToken)).IsValid) return output.WithError("validation_failed");
        var result = await store.UnlockAsync(command.Actor, command.ExpectedProtectionRevision, command.ChallengeId, command.Proof, command.RawBody, cancellationToken);
        if (result.Error is not null) return output.WithError(result.Error);
        var data = result.Data;
        if (data is null || data.AccountId == Guid.Empty || !OpaqueAccessHandle.TryHash(data.Access, out _)
            || data.ExpiresAt <= data.IssuedAt) return output.WithError("persistence_unavailable");
        return output.WithData(new VaultUnlockOutput { AccountId = data.AccountId, VaultAccess = data.Access,
            IssuedAt = data.IssuedAt, ExpiresAt = data.ExpiresAt }).WithMessage("vault_unlocked");
    }
}
