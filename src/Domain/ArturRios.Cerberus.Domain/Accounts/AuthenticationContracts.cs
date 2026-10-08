namespace ArturRios.Cerberus.Domain.Accounts;

public sealed record AuthenticationAccount(Guid Id, long Revision);
public sealed record AccountAuthenticationResult(AuthenticationAccount? Account = null, string? Error = null);

public interface IAccountAuthenticationStore
{
    Task<AccountAuthenticationResult> FindAsync(Guid identityId, CancellationToken cancellationToken);
}
