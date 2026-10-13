using System.Data.Common;
using ArturRios.Cerberus.Domain.Offline;
using ArturRios.Cerberus.Domain.Protection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace ArturRios.Cerberus.Data.Offline;

public sealed class OfflinePolicyUpdateStore(IDbContextFactory<AppDbContext> factory) : IOfflinePolicyUpdateStore
{
    public async Task<OfflinePolicyReadResult> UpdateAsync(OfflinePolicyUpdateRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request is null) return new(Error: "validation_failed");
        if (request.IdentityId == Guid.Empty) return new(Error: "authentication_required");
        if (string.IsNullOrWhiteSpace(request.Verifier)) return new(Error: "vault_access_required");
        if (!request.IsValid(DateTimeOffset.UtcNow)) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var captured = await Read(db, request, ct);
            var error = AuthorityError(captured);
            if (error is not null) return new(Error: error);
            var original = captured!;
            async Task<string?> Recheck() => RecheckError(await Read(db, request, ct), original);

            // All policy/protection/lifecycle writers share this canonical lock prefix.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={original.OwnerId} FOR UPDATE", ct);
            error = await Recheck(); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE id={original.SessionId} AND account_id={original.OwnerId} FOR UPDATE", ct);
            error = await Recheck(); if (error is not null) return new(Error: error);
            if (original.Selection is { } profile)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE id={profile} AND account_id={original.OwnerId} FOR NO KEY UPDATE", ct);
                error = await Recheck(); if (error is not null) return new(Error: error);
            }
            if (original.PolicyRevision != request.ExpectedPolicyRevision || original.PolicyRevision == ProtocolBinary.MaxInteger)
                return new(Error: "revision_conflict");
            var before = await Read(db, request, ct);
            error = RecheckError(before, original); if (error is not null) return new(Error: error);
            if (!request.IsValid(before!.DatabaseNow)) return new(Error: "validation_failed");

            var parameters = Parameters(request).Concat(new object[]
            {
                new NpgsqlParameter("owner", original.OwnerId), new NpgsqlParameter("account_revision", original.AccountRevision),
                new NpgsqlParameter("generation", original.RevocationGeneration), new NpgsqlParameter("previous", request.ExpectedPolicyRevision),
                new NpgsqlParameter("enabled", request.RenewalEnabled),
                new NpgsqlParameter("interval", NpgsqlDbType.Interval) { Value = request.RenewalInterval is { } interval ? interval : DBNull.Value }
            }).ToArray();
            // No lock wait remains at this point. Statement time is the authorization
            // linearization point; transaction-start now() would reuse an older instant.
            var changed = await db.Database.ExecuteSqlRawAsync("""
                UPDATE cerberus.account a SET renewal_enabled=@enabled,renewal_interval=@interval,policy_revision=policy_revision+1
                WHERE a.id=@owner AND a.heimdall_public_id=@actor AND a.state=0
                  AND a.revision=@account_revision AND a.policy_revision=@previous AND a.revocation_generation=@generation
                  AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)
                  AND (NOT @enabled OR statement_timestamp()+@interval<='9999-12-31 23:59:59.999999+00'::timestamptz)
                  AND EXISTS(SELECT FROM cerberus.vault_access_session s WHERE s.account_id=a.id AND s.handle_verifier=@verifier
                    AND NOT s.revoked AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation
                    AND s.issued_at<=statement_timestamp()
                    AND CASE WHEN a.renewal_enabled THEN s.expires_at>statement_timestamp() ELSE s.expires_at IS NULL END
                    AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile p WHERE p.id=s.profile_id AND p.account_id=a.id
                      AND p.deleted_at IS NULL AND p.purge_at IS NULL
                      AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id))))
                """, parameters, ct);
            if (changed != 1)
            {
                if (changed != 0) return new(Error: "persistence_unavailable");
                var current = await Read(db, request, ct);
                return new(Error: RecheckError(current, original)
                    ?? (current is not null && !request.IsValid(current.DatabaseNow) ? "validation_failed" : "persistence_unavailable"));
            }

            // The policy change deliberately makes this session's version stale.
            // Check its original metadata, not its resulting authorization predicate.
            var post = await Read(db, request, ct);
            if (post is null || !post.ActorActive || post.OwnerId != original.OwnerId || post.PublicId != original.PublicId
                || post.AccountRevision != original.AccountRevision || post.RevocationGeneration != original.RevocationGeneration
                || post.PolicyRevision != request.ExpectedPolicyRevision + 1 || post.RenewalEnabled != request.RenewalEnabled
                || post.RenewalInterval != request.RenewalInterval || !SameSessionAndSelection(post, original))
                return new(Error: "persistence_unavailable");
            await tx.CommitAsync(ct);
            return new(new(post.RenewalEnabled, post.RenewalInterval, post.PolicyRevision, post.RevocationGeneration));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        catch (Exception e) when (e is DbException or DbUpdateException or TimeoutException
            || e is InvalidOperationException { InnerException: DbException or DbUpdateException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }

    private static string? AuthorityError(Snapshot? s)
    {
        if (s is null || !s.ActorActive) return "not_found";
        if (!s.SessionAllowed) return "vault_access_denied";
        if (s.PublicId == Guid.Empty || s.AccountRevision is <= 0 or > ProtocolBinary.MaxInteger
            || s.PolicyRevision is <= 0 or > ProtocolBinary.MaxInteger || s.RevocationGeneration is <= 0 or > ProtocolBinary.MaxInteger
            || (s.RenewalEnabled ? s.RenewalInterval is null || s.RenewalInterval <= TimeSpan.Zero : s.RenewalInterval is not null))
            return "persistence_unavailable";
        return null;
    }
    private static string? RecheckError(Snapshot? current, Snapshot captured)
    {
        if (current is null || !current.ActorActive) return "revision_conflict";
        if (!current.SessionAllowed) return "vault_access_denied";
        if (current.OwnerId != captured.OwnerId || current.PublicId != captured.PublicId
            || current.AccountRevision != captured.AccountRevision || current.PolicyRevision != captured.PolicyRevision
            || current.RevocationGeneration != captured.RevocationGeneration || current.RenewalEnabled != captured.RenewalEnabled
            || current.RenewalInterval != captured.RenewalInterval || !SameSessionAndSelection(current, captured))
            return "revision_conflict";
        return AuthorityError(current);
    }
    private static bool SameSessionAndSelection(Snapshot a, Snapshot b) => a.SessionId == b.SessionId
        && a.Selection == b.Selection && a.SessionIssuedAt == b.SessionIssuedAt && a.SessionExpiresAt == b.SessionExpiresAt
        && a.SessionPolicyRevision == b.SessionPolicyRevision && a.SessionGeneration == b.SessionGeneration
        && a.SessionRevoked == b.SessionRevoked && a.ProfileId == b.ProfileId && a.ProfileRevision == b.ProfileRevision
        && a.ProfileDeletedAt == b.ProfileDeletedAt && a.ProfilePurgeAt == b.ProfilePurgeAt
        && a.SelectionActive == b.SelectionActive;
    private static object[] Parameters(OfflinePolicyUpdateRequest r) =>
        [new NpgsqlParameter("actor", r.IdentityId), new NpgsqlParameter("verifier", r.Verifier)];
    private static async Task<Snapshot?> Read(AppDbContext db, OfflinePolicyUpdateRequest r, CancellationToken ct)
    {
        try
        {
            return (await db.Database.SqlQueryRaw<Snapshot>(SnapshotSql, Parameters(r)).ToListAsync(ct)).SingleOrDefault();
        }
        catch (Exception e) when (e is InvalidCastException or OverflowException
            || e is InvalidOperationException { InnerException: InvalidCastException or OverflowException })
        {
            // Provider conversion failures are corrupt persisted metadata. Keep
            // this mapping at materialization so unrelated programming errors escape.
            throw new DbUpdateException("Stored offline policy metadata could not be decoded.", e);
        }
    }

    private sealed class Snapshot
    {
        public long OwnerId { get; set; } public Guid PublicId { get; set; } public long AccountRevision { get; set; }
        public bool ActorActive { get; set; } public bool SessionAllowed { get; set; }
        public bool SelectionActive { get; set; }
        public bool RenewalEnabled { get; set; } public TimeSpan? RenewalInterval { get; set; }
        public long PolicyRevision { get; set; } public long RevocationGeneration { get; set; }
        public long? SessionId { get; set; } public long? Selection { get; set; } public bool? SessionRevoked { get; set; }
        public DateTimeOffset? SessionIssuedAt { get; set; } public DateTimeOffset? SessionExpiresAt { get; set; }
        public long? SessionPolicyRevision { get; set; } public long? SessionGeneration { get; set; }
        public Guid? ProfileId { get; set; } public long? ProfileRevision { get; set; }
        public DateTimeOffset? ProfileDeletedAt { get; set; } public DateTimeOffset? ProfilePurgeAt { get; set; }
        public DateTimeOffset DatabaseNow { get; set; }
    }
    private const string SnapshotSql = """
        SELECT a.id owner_id,a.public_id,a.revision account_revision,a.renewal_enabled,a.renewal_interval,
          a.policy_revision,a.revocation_generation,statement_timestamp() database_now,
          a.state=0 AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id) actor_active,
          COALESCE(s.id IS NOT NULL AND NOT s.revoked AND s.policy_revision>0 AND s.revocation_generation>0
            AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation
            AND s.issued_at<=statement_timestamp()
            AND CASE WHEN a.renewal_enabled THEN s.expires_at>statement_timestamp() ELSE s.expires_at IS NULL END
            AND (s.profile_id IS NULL OR (p.id IS NOT NULL AND p.deleted_at IS NULL AND p.purge_at IS NULL
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id))),false) session_allowed,
          s.id session_id,s.profile_id selection,s.revoked session_revoked,s.issued_at session_issued_at,s.expires_at session_expires_at,
          s.policy_revision session_policy_revision,s.revocation_generation session_generation,
          (s.profile_id IS NULL OR (p.id IS NOT NULL AND p.deleted_at IS NULL AND p.purge_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id))) selection_active,
          p.public_id profile_id,p.revision profile_revision,p.deleted_at profile_deleted_at,p.purge_at profile_purge_at
        FROM cerberus.account a LEFT JOIN cerberus.vault_access_session s ON s.account_id=a.id AND s.handle_verifier=@verifier
        LEFT JOIN cerberus.profile p ON p.id=s.profile_id AND p.account_id=a.id WHERE a.heimdall_public_id=@actor
        """;
}
