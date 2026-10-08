using ArturRios.Mediator.Query;

namespace ArturRios.Cerberus.Query.Accounts;

public sealed class GetAccountQuery(Guid identityId, string? vaultAccess) : BaseQuery
{
    public Guid IdentityId { get; } = identityId;
    public string? VaultAccess { get; } = vaultAccess;
}
