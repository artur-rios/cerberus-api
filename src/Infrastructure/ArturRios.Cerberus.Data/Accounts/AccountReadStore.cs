using System.Data.Common;
using ArturRios.Cerberus.Domain.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Accounts;

public sealed class AccountReadStore(IDbContextFactory<AppDbContext> factory) : IAccountReadStore
{
    public async Task<AccountReadResult> ReadAsync(Guid identityId, string verifier, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            // One database snapshot; the CASE projection withholds ciphertext unless the
            // current account, tombstone and account-wide session predicates all permit it.
            var snapshot = await (from account in context.Accounts.AsNoTracking()
                where account.HeimdallPublicId == identityId
                let erased = context.TerminalErasures.Any(e => e.ResourceId == account.PublicId)
                let allowed = context.VaultAccessSessions.Any(s => s.AccountId == account.Id && s.HandleVerifier == verifier
                    && s.ProfileId == null && !s.Revoked && s.IssuedAt <= now
                    && s.PolicyRevision > 0 && s.RevocationGeneration > 0
                    && s.PolicyRevision == account.PolicyRevision && s.RevocationGeneration == account.RevocationGeneration
                    && (account.RenewalEnabled ? s.ExpiresAt > now : s.ExpiresAt == null))
                select new { account.PublicId, account.Revision, account.State, Erased = erased,
                    Details = account.State == AccountState.Active && !erased && allowed ? account.DetailsEnvelope : null })
                .SingleOrDefaultAsync(cancellationToken);
            if (snapshot is null || snapshot.State != AccountState.Active || snapshot.Erased) return new(Error: "not_found");
            if (snapshot.Details is null) return new(Error: "vault_access_denied");
            return new(new AccountSnapshot(snapshot.PublicId, snapshot.Revision, snapshot.State, snapshot.Details));
        }
        catch (Exception exception) when (exception is DbException or TimeoutException
            || exception is InvalidOperationException { InnerException: DbException or TimeoutException })
        {
            return new(Error: "persistence_unavailable");
        }
    }
}
