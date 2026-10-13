using System.Data.Common;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Offline;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Offline;

public sealed class OfflinePolicyReadStore(IDbContextFactory<AppDbContext> factory) : IOfflinePolicyReadStore
{
    public async Task<OfflinePolicyReadResult> ReadAsync(Guid identityId, string verifier, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            // Current lifecycle, access and metadata come from one database snapshot.
            // CASE expressions withhold the policy on denial; no encrypted material is selected.
            var row = await (from account in db.Accounts.AsNoTracking()
                where account.HeimdallPublicId == identityId
                let erased = db.TerminalErasures.Any(e => e.ResourceKind == "account" && e.ResourceId == account.PublicId)
                let allowed = account.State == AccountState.Active && !erased && db.VaultAccessSessions.Any(s =>
                    s.AccountId == account.Id && s.HandleVerifier == verifier && !s.Revoked && s.IssuedAt <= now
                    && s.PolicyRevision > 0 && s.PolicyRevision == account.PolicyRevision
                    && s.RevocationGeneration > 0 && s.RevocationGeneration == account.RevocationGeneration
                    && (account.RenewalEnabled ? s.ExpiresAt > now : s.ExpiresAt == null)
                    && (s.ProfileId == null || db.Profiles.Any(p => p.Id == s.ProfileId && p.AccountId == account.Id
                        && p.DeletedAt == null && p.PurgeAt == null
                        && !db.TerminalErasures.Any(e => e.ResourceKind == "profile" && e.ResourceId == p.PublicId))))
                select new
                {
                    account.State, Erased = erased,
                    Enabled = allowed ? (bool?)account.RenewalEnabled : null,
                    Interval = allowed ? account.RenewalInterval : null,
                    Revision = allowed ? account.PolicyRevision : 0,
                    Generation = allowed ? account.RevocationGeneration : 0
                }).SingleOrDefaultAsync(cancellationToken);
            if (row is null || row.State != AccountState.Active || row.Erased) return new(Error: "not_found");
            if (row.Enabled is null) return new(Error: "vault_access_denied");
            return new(new(row.Enabled.Value, row.Interval, row.Revision, row.Generation));
        }
        catch (Exception exception) when (exception is DbException or TimeoutException
            || exception is InvalidOperationException { InnerException: DbException or TimeoutException })
        {
            return new(Error: "persistence_unavailable");
        }
    }
}
