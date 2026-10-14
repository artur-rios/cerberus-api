using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Offline;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Offline;

public sealed class SetOfflinePolicyHandler(IValidator<SetOfflinePolicyCommand> validator,
    IOfflinePolicyUpdateStore store, TimeProvider clock) : ICommandHandlerAsync<SetOfflinePolicyCommand, SetOfflinePolicyOutput>
{
    public async Task<DataOutput<SetOfflinePolicyOutput?>> HandleAsync(SetOfflinePolicyCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<SetOfflinePolicyOutput?>.New;
        if (command.IdentityId == Guid.Empty) return output.WithError("authentication_required");
        if (command.VaultAccess is null) return output.WithError("vault_access_required");
        if (!OpaqueAccessHandle.TryHash(command.VaultAccess, out var verifier)) return output.WithError("validation_failed");
        if (!(await validator.ValidateAsync(command, cancellationToken)).IsValid) return output.WithError("validation_failed");
        var request = new OfflinePolicyUpdateRequest(command.IdentityId, verifier, command.ExpectedPolicyRevision,
            command.RenewalEnabled, command.RenewalInterval);
        if (!request.IsValid(clock.GetUtcNow())) return output.WithError("validation_failed");
        var result = await store.UpdateAsync(request, cancellationToken);
        if (result.Error is not null) return output.WithError(result.Error);
        var policy = result.Policy;
        if (policy is null || policy.PolicyRevision is <= 0 or > ProtocolBinary.MaxInteger
            || policy.PolicyRevision != command.ExpectedPolicyRevision + 1
            || policy.RevocationGeneration is <= 0 or > ProtocolBinary.MaxInteger
            || policy.RenewalEnabled != command.RenewalEnabled || policy.RenewalInterval != command.RenewalInterval)
            return output.WithError("persistence_unavailable");
        return output.WithData(new SetOfflinePolicyOutput
        {
            RenewalEnabled = policy.RenewalEnabled, RenewalInterval = policy.RenewalInterval,
            PolicyRevision = policy.PolicyRevision, RevocationGeneration = policy.RevocationGeneration
        }).WithMessage("offline_policy_updated");
    }
}
