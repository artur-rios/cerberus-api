using ArturRios.Mediator.Query;

namespace ArturRios.Cerberus.Query.Offline;

public sealed class GetOfflinePolicyQuery(Guid identityId, string? vaultAccess) : BaseQuery
{
    public Guid IdentityId { get; } = identityId;
    public string? VaultAccess { get; } = vaultAccess;
}
