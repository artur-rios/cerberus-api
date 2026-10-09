namespace ArturRios.Cerberus.Domain.Accounts;

public sealed record AccountUpdateRequest(Guid IdentityId, string Verifier, long ExpectedRevision,
    byte[] DetailsEnvelope, DateTimeOffset Now);
public sealed record AccountUpdateResult(Guid? Id = null, long Revision = 0, string? Error = null);

public interface IAccountUpdateStore
{
    Task<AccountUpdateResult> UpdateAsync(AccountUpdateRequest request, CancellationToken cancellationToken);
}
