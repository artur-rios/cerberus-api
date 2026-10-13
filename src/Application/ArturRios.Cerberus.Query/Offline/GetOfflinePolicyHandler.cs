using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Offline;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;

namespace ArturRios.Cerberus.Query.Offline;

public sealed class GetOfflinePolicyHandler(IOfflinePolicyReadStore policies, TimeProvider clock)
    : IQueryHandlerAsync<GetOfflinePolicyQuery, OfflinePolicyOutput>
{
    public async Task<DataOutput<OfflinePolicyOutput?>> HandleAsync(GetOfflinePolicyQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<OfflinePolicyOutput?>.New;
        if (query.IdentityId == Guid.Empty) return output.WithError("authentication_required");
        if (query.VaultAccess is null) return output.WithError("vault_access_required");
        if (!OpaqueAccessHandle.TryHash(query.VaultAccess, out var verifier))
            return output.WithError("validation_failed");
        var result = await policies.ReadAsync(query.IdentityId, verifier, clock.GetUtcNow(), cancellationToken);
        if (result.Error is not null) return output.WithError(result.Error);
        var policy = result.Policy;
        if (policy is null || policy.PolicyRevision <= 0 || policy.RevocationGeneration <= 0
            || (policy.RenewalEnabled ? policy.RenewalInterval is null || policy.RenewalInterval <= TimeSpan.Zero
                : policy.RenewalInterval is not null))
            return output.WithError("persistence_unavailable");
        return output.WithData(new OfflinePolicyOutput
        {
            RenewalEnabled = policy.RenewalEnabled, RenewalInterval = policy.RenewalInterval,
            PolicyRevision = policy.PolicyRevision, RevocationGeneration = policy.RevocationGeneration
        }).WithMessage("offline_policy_found");
    }
}
