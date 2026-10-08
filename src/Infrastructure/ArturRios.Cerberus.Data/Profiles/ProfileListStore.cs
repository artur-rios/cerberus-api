using System.Data.Common;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Profiles;

public sealed class ProfileListStore(IDbContextFactory<AppDbContext> factory) : IProfileListStore
{
    public async Task<VaultResult<ProfileListPage>> ListAsync(ProfileListRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            // All visibility and authorization predicates, the boundary, and the page are
            // evaluated in one SQL snapshot. UtcNow translates to PostgreSQL now(); there
            // is no enclosing transaction whose start time could precede this statement.
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
            var visible = db.Profiles.AsNoTracking().Where(p => p.DeletedAt == null
                && !db.TerminalErasures.Any(e => e.ResourceId == p.PublicId)
                && sessions.Any(s => s.AccountId == p.AccountId && (s.ProfileId == null || s.ProfileId == p.Id)));
            var page = visible.Where(p => p.ServerSequence > request.After
                    && p.ServerSequence <= (request.Boundary ?? visible.Max(x => (long?)x.ServerSequence) ?? 0))
                .OrderBy(p => p.ServerSequence);
            var rows = ProfileProjectionQuery.Select(db,page.Take(request.PageSize));
            var result = await db.Accounts.AsNoTracking().Where(a => a.HeimdallPublicId == request.Actor)
                .Select(a => new
                {
                    a.State, Erased = db.TerminalErasures.Any(e => e.ResourceId == a.PublicId),
                    Allowed = sessions.Any(),
                    // Validate the permitted inventory before keyset filtering: zero
                    // values disappear at After=0, and equal values can straddle pages.
                    CorruptOrdering = visible.Any(p => p.ServerSequence <= 0 || p.ServerSequence > ProtocolBinary.MaxInteger
                        || visible.Any(other => other.Id != p.Id && other.ServerSequence == p.ServerSequence)),
                    Boundary = request.Boundary ?? visible.Max(p => (long?)p.ServerSequence) ?? 0,
                    HasMore = page.Skip(request.PageSize).Any(),
                    Items = rows.ToList()
                }).AsSingleQuery().SingleOrDefaultAsync(cancellationToken);
            if (result is null || result.State != AccountState.Active || result.Erased) return new(Error: "not_found");
            if (!result.Allowed) return new(Error: "vault_access_denied");
            if (result.CorruptOrdering) return new(Error: "persistence_unavailable");
            return new(new(result.Items, result.Boundary, result.HasMore));
        }
        catch (Exception exception) when (exception is DbException or TimeoutException
            || exception is InvalidOperationException { InnerException: DbException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }
}
