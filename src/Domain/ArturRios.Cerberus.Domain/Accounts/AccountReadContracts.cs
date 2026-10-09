namespace ArturRios.Cerberus.Domain.Accounts;

public sealed record AccountSnapshot(Guid Id, long Revision, AccountState State, byte[] DetailsEnvelope);
public sealed record AccountReadResult(AccountSnapshot? Account = null, string? Error = null);

public interface IAccountReadStore
{
    Task<AccountReadResult> ReadAsync(Guid identityId, string verifier, DateTimeOffset now, CancellationToken cancellationToken);
}
