using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Offline;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Offline;

public sealed class RenewOfflineAuthorizationHandler(IValidator<RenewOfflineAuthorizationCommand> validator,
    IOfflineAuthorizationStore store) : ICommandHandlerAsync<RenewOfflineAuthorizationCommand, OfflineAuthorizationOutput>
{
    public async Task<DataOutput<OfflineAuthorizationOutput?>> HandleAsync(RenewOfflineAuthorizationCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<OfflineAuthorizationOutput?>.New;
        if (command.IdentityId == Guid.Empty) return output.WithError("authentication_required");
        if (command.VaultAccess is null) return output.WithError("vault_access_required");
        if (!OpaqueAccessHandle.TryHash(command.VaultAccess, out var verifier)
            || !(await validator.ValidateAsync(command, cancellationToken)).IsValid) return output.WithError("validation_failed");
        var result = await store.RenewAsync(new(command.IdentityId, verifier, command.ProfileId, command.ExpectedProfileRevision), cancellationToken);
        if (result.Error is not null) return output.WithError(result.Error);
        var d = result.Details;
        if (d?.Snapshot?.IsValid() != true || d.Snapshot.IdentityId != command.IdentityId || d.Snapshot.ProfileId != command.ProfileId
            || d.Snapshot.ProtectionRevision != command.ExpectedProfileRevision || d.Lease?.MatchesMetadata(d.Snapshot) != true)
            return output.WithError("persistence_unavailable");
        var s = d.Snapshot;
        return output.WithData(new OfflineAuthorizationOutput
        {
            Lease = d.Lease.Compact, AccountId = s.AccountId, ProfileId = s.ProfileId, KeyEpoch = s.KeyEpoch,
            ProtectionRevision = s.ProtectionRevision, PolicyRevision = s.PolicyRevision, RevocationGeneration = s.RevocationGeneration,
            GrantRevisions = s.GrantRevisions.OrderBy(g => g.GrantId.ToString("D"), StringComparer.Ordinal).ToArray(),
            RenewalEnabled = s.RenewalEnabled, IssuedAt = d.Lease.IssuedAt, ExpiresAt = d.Lease.ExpiresAt
        }).WithMessage("offline_authorization_renewed");
    }
}
