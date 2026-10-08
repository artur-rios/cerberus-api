using System.Data.Common;
using ArturRios.Cerberus.Domain.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Accounts;

public sealed class AccountUpdateStore(IDbContextFactory<AppDbContext> factory) : IAccountUpdateStore
{
    public async Task<AccountUpdateResult> UpdateAsync(AccountUpdateRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            var owned = context.Accounts.AsNoTracking().Where(a => a.HeimdallPublicId == request.IdentityId);
            // Null selects PostgreSQL's current statement/transaction time. The
            // standalone UPDATE must not reuse an older request-time timestamp.
            IQueryable<Account> AuthorizedAt(DateTimeOffset? at) => owned.Where(a => a.State == AccountState.Active
                && !context.TerminalErasures.Any(e => e.ResourceId == a.PublicId)
                && context.VaultAccessSessions.Any(s => s.AccountId == a.Id && s.HandleVerifier == request.Verifier
                    && s.ProfileId == null && !s.Revoked && s.IssuedAt <= (at ?? DateTimeOffset.UtcNow)
                    && s.PolicyRevision > 0 && s.RevocationGeneration > 0
                    && s.PolicyRevision == a.PolicyRevision && s.RevocationGeneration == a.RevocationGeneration
                    && (a.RenewalEnabled ? s.ExpiresAt > (at ?? DateTimeOffset.UtcNow) : s.ExpiresAt == null)));
            var permitted = AuthorizedAt(request.Now);
            // Read only authorization metadata; the current envelope is never retrieved.
            var snapshot = await owned.Select(a => new
            {
                a.PublicId, a.Revision, a.State,
                Erased = context.TerminalErasures.Any(e => e.ResourceId == a.PublicId),
                Allowed = permitted.Any(p => p.Id == a.Id)
            }).SingleOrDefaultAsync(cancellationToken);
            if (snapshot is null || snapshot.State != AccountState.Active || snapshot.Erased) return new(Error: "not_found");
            if (!snapshot.Allowed) return new(Error: "vault_access_denied");
            if (snapshot.Revision != request.ExpectedRevision || snapshot.Revision == long.MaxValue) return new(Error: "revision_conflict");

            // The UPDATE is the linearization point. All authorization predicates and
            // the expected revision are rechecked atomically, including competing edits.
            var changed = await AuthorizedAt(null).Where(a => a.PublicId == snapshot.PublicId && a.Revision == request.ExpectedRevision)
                .ExecuteUpdateAsync(set => set.SetProperty(a => a.DetailsEnvelope, request.DetailsEnvelope)
                    .SetProperty(a => a.Revision, a => a.Revision + 1), cancellationToken);
            return changed == 1 ? new(snapshot.PublicId, request.ExpectedRevision + 1) : new(Error: "revision_conflict");
        }
        catch (Exception exception) when (exception is DbException or TimeoutException
            || exception is InvalidOperationException { InnerException: DbException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(Error: "persistence_unavailable"); }
    }
}
