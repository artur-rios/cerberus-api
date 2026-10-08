using System.Data.Common;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Profiles;

public sealed class ProfileReadStore(IDbContextFactory<AppDbContext> factory) : IProfileReadStore
{
    public async Task<VaultResult<ProfileReadDetails>> ReadAsync(ProfileReadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            // One statement, no earlier transaction: UtcNow translates to PostgreSQL
            // now() at this snapshot. A valid selection never authorizes another profile.
            var sessions = from s in db.VaultAccessSessions
                join a in db.Accounts on s.AccountId equals a.Id
                where a.HeimdallPublicId == request.Actor && a.State == AccountState.Active
                    && !db.TerminalErasures.Any(e => e.ResourceId == a.PublicId)
                    && s.HandleVerifier == request.AccessVerifier && !s.Revoked
                    && s.IssuedAt <= DateTimeOffset.UtcNow && s.PolicyRevision > 0 && s.RevocationGeneration > 0
                    && s.PolicyRevision == a.PolicyRevision && s.RevocationGeneration == a.RevocationGeneration
                    && (a.RenewalEnabled ? s.ExpiresAt > DateTimeOffset.UtcNow : s.ExpiresAt == null)
                    && (s.ProfileId == null || db.Profiles.Any(p => p.Id == s.ProfileId && p.AccountId == a.Id
                        && p.DeletedAt == null && !db.TerminalErasures.Any(e => e.ResourceId == p.PublicId)))
                select s;
            var visible = db.Profiles.AsNoTracking().Where(p => p.PublicId == request.ProfileId && p.DeletedAt == null
                && !db.TerminalErasures.Any(e => e.ResourceId == p.PublicId)
                && sessions.Any(s => s.AccountId == p.AccountId && (s.ProfileId == null || s.ProfileId == p.Id)));
            var result = await db.Accounts.AsNoTracking().Where(a => a.HeimdallPublicId == request.Actor)
                .Select(a => new
                {
                    a.State, Erased = db.TerminalErasures.Any(e => e.ResourceId == a.PublicId), Allowed = sessions.Any(),
                    Items = visible.Select(p => new ProfileListRow(p.PublicId, p.Revision, p.ServerSequence,
                        p.EditedAt, p.Envelope, p.KeyWrappers)).ToList()
                }).AsSingleQuery().SingleOrDefaultAsync(cancellationToken);
            if (result is null || result.State != AccountState.Active || result.Erased) return new(Error: "not_found");
            if (!result.Allowed) return new(Error: "vault_access_denied");
            if (result.Items.Count == 0) return new(Error: "not_found");
            // PublicId is unique. Relationships do not exist yet; UC09 rejects
            // unresolved nonempty links. Extend this projection with visible joins
            // when resource/membership entities arrive, before those flows release.
            return new(new(result.Items[0], [], [], []));
        }
        catch (Exception exception) when (exception is DbException or TimeoutException
            || exception is InvalidOperationException { InnerException: DbException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }
}
