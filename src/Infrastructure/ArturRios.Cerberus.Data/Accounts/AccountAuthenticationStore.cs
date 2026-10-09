using System.Data.Common;
using ArturRios.Cerberus.Domain.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Accounts;

public sealed class AccountAuthenticationStore(IDbContextFactory<AppDbContext> factory) : IAccountAuthenticationStore
{
    public async Task<AccountAuthenticationResult> FindAsync(Guid identityId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            var account = await context.Accounts.AsNoTracking().Where(x => x.HeimdallPublicId == identityId)
                .Select(x => new { x.PublicId, x.Revision, x.State, Erased = context.TerminalErasures.Any(e => (e.ResourceKind == "account" && e.ResourceId == x.PublicId)) })
                .SingleOrDefaultAsync(cancellationToken);
            if (account is null) return new();
            if (account.State != AccountState.Active || account.Erased) return new(Error: "authentication_required");
            return new(new AuthenticationAccount(account.PublicId, account.Revision));
        }
        catch (Exception exception) when (exception is DbException or TimeoutException
            || exception is InvalidOperationException { InnerException: DbException or TimeoutException })
        {
            return new(Error: "persistence_unavailable");
        }
    }
}
