using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Offline;
using ArturRios.Cerberus.Domain.Protection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.Data.Offline;

public sealed class OfflineAuthorizationStore(IDbContextFactory<AppDbContext> factory, IOfflineLeaseSigner signer) : IOfflineAuthorizationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false };

    public async Task<OfflineAuthorizationResult> RenewAsync(OfflineAuthorizationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request is null) return new(Error: "validation_failed");
        if (request.IdentityId == Guid.Empty) return new(Error: "authentication_required");
        if (string.IsNullOrWhiteSpace(request.Verifier)) return new(Error: "vault_access_required");
        if (!request.IsValid()) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var initial = await Read(db, request, ct);
            var error = AuthorityError(initial);
            if (error is not null) return new(Error: error);
            var original = initial!;
            var related = JsonSerializer.Deserialize<RelatedCollection[]>(original.Related, Json) ?? throw new JsonException();
            async Task<string?> Recheck() => RecheckError(await Read(db, request, ct), original);
            var accounts = related.Select(c => c.OwnerPublicId).Append(original.AccountId).Distinct().ToArray();
            // Match the public-id order of sharing/lifecycle writers. No lock
            // upgrade or write is needed to issue an immutable authorization.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE public_id=ANY({accounts}) ORDER BY public_id FOR SHARE", ct);
            error = await Recheck(); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE id={original.SessionId} AND account_id={original.OwnerId} FOR SHARE", ct);
            error = await Recheck(); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE id={original.ProfileInternalId} AND account_id={original.OwnerId} FOR SHARE", ct);
            error = await Recheck(); if (error is not null) return new(Error: error);
            foreach (var collection in related.OrderBy(c => c.PublicId.ToString("D"), StringComparer.Ordinal))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE id={collection.Id} FOR SHARE", ct);
                error = await Recheck(); if (error is not null) return new(Error: error);
                if (collection.GrantId is { } grantId)
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection_grant WHERE id={grantId} AND collection_id={collection.Id} FOR SHARE", ct);
                    error = await Recheck(); if (error is not null) return new(Error: error);
                }
            }
            if (original.ProfileRevision != request.ExpectedProfileRevision) return new(Error: "revision_conflict");
            var epoch = await KeyEpoch(db, original, ct);
            if (epoch is <= 0 or > ProtocolBinary.MaxInteger) return new(Error: "persistence_unavailable");
            var grants = new List<OfflineGrantRevision>();
            foreach (var c in related.Where(c => c.OwnerPublicId != original.AccountId && c.CollectionActive && c.GrantActive))
            {
                if (c.PublicId == Guid.Empty || c.OwnerPublicId == Guid.Empty || c.Revision is <= 0 or > ProtocolBinary.MaxInteger
                    || c.KeyEpoch is <= 0 or > ProtocolBinary.MaxInteger || c.GrantPublicId is null)
                    return new(Error: "persistence_unavailable");
                if (c.GrantPublicId == Guid.Empty || c.GrantRevision is null or <= 0 or > ProtocolBinary.MaxInteger)
                    return new(Error: "persistence_unavailable");
                grants.Add(new(c.GrantPublicId!.Value, c.GrantRevision.Value));
            }
            var before = await Read(db, request, ct);
            error = RecheckError(before, original); if (error is not null) return new(Error: error);
            var snapshot = new OfflineLeaseSnapshot(request.IdentityId, original.AccountId, request.ProfileId, epoch, original.ProfileRevision!.Value,
                original.PolicyRevision, original.Generation, grants.OrderBy(g => g.GrantId.ToString("D"), StringComparer.Ordinal).ToArray(),
                original.RenewalEnabled, original.RenewalInterval, before!.DatabaseNow);
            if (!snapshot.IsValid()) return new(Error: "persistence_unavailable");
            ct.ThrowIfCancellationRequested();
            var lease = signer.Sign(snapshot);
            ct.ThrowIfCancellationRequested();
            if (lease?.MatchesMetadata(snapshot) != true) return new(Error: "persistence_unavailable");
            // A slow signer may outlive online authority. Revalidate before any
            // signed result leaves the transaction; cached leases cannot renew it.
            error = await Recheck(); if (error is not null) return new(Error: error);
            if (await KeyEpoch(db, original, ct) != epoch) return new(Error: "persistence_unavailable");
            await tx.CommitAsync(ct);
            return new(new(snapshot, lease));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        catch (Exception e) when (e is DbException or DbUpdateException or TimeoutException or JsonException
            || e is InvalidOperationException { InnerException: DbException or DbUpdateException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }

    private static string? AuthorityError(Snapshot? s)
    {
        if (s is null || !s.ActorActive) return "not_found";
        if (!s.SessionAllowed) return "vault_access_denied";
        if (!s.ProfileActive || s.Selection is not null && s.Selection != s.ProfileInternalId) return "not_found";
        if (s.AccountId == Guid.Empty || s.ProfileId == Guid.Empty || s.AccountRevision is <= 0 or > ProtocolBinary.MaxInteger
            || s.ProfileRevision is <= 0 or > ProtocolBinary.MaxInteger || s.PolicyRevision is <= 0 or > ProtocolBinary.MaxInteger
            || s.Generation is <= 0 or > ProtocolBinary.MaxInteger
            || (s.RenewalEnabled ? s.RenewalInterval is not { Ticks: > 0 } : s.RenewalInterval is not null)) return "persistence_unavailable";
        return null;
    }
    private static string? RecheckError(Snapshot? current, Snapshot captured)
    {
        if (current is null || !current.ActorActive || !current.ProfileActive) return "revision_conflict";
        var error = AuthorityError(current); if (error is not null) return error;
        return current.OwnerId != captured.OwnerId || current.AccountId != captured.AccountId || current.AccountRevision != captured.AccountRevision
            || current.ProfileInternalId != captured.ProfileInternalId || current.ProfileId != captured.ProfileId || current.ProfileRevision != captured.ProfileRevision
            || current.SessionId != captured.SessionId || current.Selection != captured.Selection || current.SessionIssuedAt != captured.SessionIssuedAt
            || current.SessionExpiresAt != captured.SessionExpiresAt || current.PolicyRevision != captured.PolicyRevision || current.Generation != captured.Generation
            || current.RenewalEnabled != captured.RenewalEnabled || current.RenewalInterval != captured.RenewalInterval || current.Related != captured.Related
            ? "revision_conflict" : null;
    }
    private static async Task<Snapshot?> Read(AppDbContext db, OfflineAuthorizationRequest r, CancellationToken ct)
    {
        try
        {
            return (await db.Database.SqlQueryRaw<Snapshot>(SnapshotSql, new NpgsqlParameter("actor", r.IdentityId),
                new NpgsqlParameter("verifier", r.Verifier), new NpgsqlParameter("profile", r.ProfileId)).ToListAsync(ct)).SingleOrDefault();
        }
        catch (Exception e) when (e is InvalidCastException or OverflowException
            || e is InvalidOperationException { InnerException: InvalidCastException or OverflowException })
        { throw new DbUpdateException("Stored offline authorization metadata could not be decoded.", e); }
    }
    private static Task<long> KeyEpoch(AppDbContext db, Snapshot s, CancellationToken ct) => db.Database.SqlQuery<long>($"""
        SELECT CASE WHEN convert_from(envelope,'UTF8')::jsonb->>'format'='cerberus-content-v1'
          AND jsonb_typeof(convert_from(envelope,'UTF8')::jsonb->'keyEpoch')='number'
          AND (convert_from(envelope,'UTF8')::jsonb->>'keyEpoch')~'^[1-9][0-9]*$'
          THEN (convert_from(envelope,'UTF8')::jsonb->>'keyEpoch')::bigint ELSE 0 END AS "Value"
        FROM cerberus.profile WHERE id={s.ProfileInternalId} AND account_id={s.OwnerId}
        """).SingleAsync(ct);
    private sealed class Snapshot
    {
        public long OwnerId { get; set; } public Guid AccountId { get; set; } public long AccountRevision { get; set; }
        public bool ActorActive { get; set; } public bool SessionAllowed { get; set; } public bool ProfileActive { get; set; }
        public long? SessionId { get; set; } public long? Selection { get; set; }
        public DateTimeOffset? SessionIssuedAt { get; set; } public DateTimeOffset? SessionExpiresAt { get; set; }
        public long? ProfileInternalId { get; set; } public Guid? ProfileId { get; set; } public long? ProfileRevision { get; set; }
        public long PolicyRevision { get; set; } public long Generation { get; set; }
        public bool RenewalEnabled { get; set; } public TimeSpan? RenewalInterval { get; set; }
        public DateTimeOffset DatabaseNow { get; set; } public string Related { get; set; } = "[]";
    }
    private sealed record RelatedCollection(long Id, Guid PublicId, Guid OwnerPublicId, long Revision, long KeyEpoch,
        bool CollectionActive, bool GrantActive, long? GrantId, Guid? GrantPublicId, long? GrantRevision);
    private const string SnapshotSql = """
        SELECT a.id owner_id,a.public_id account_id,a.revision account_revision,a.policy_revision,a.revocation_generation generation,
          a.renewal_enabled,a.renewal_interval,statement_timestamp() database_now,
          a.state=0 AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id) actor_active,
          COALESCE(s.id IS NOT NULL AND NOT s.revoked AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation
            AND s.issued_at<=statement_timestamp() AND CASE WHEN a.renewal_enabled THEN s.expires_at>statement_timestamp() ELSE s.expires_at IS NULL END,false) session_allowed,
          s.id session_id,s.profile_id selection,s.issued_at session_issued_at,s.expires_at session_expires_at,
          p.id profile_internal_id,p.public_id profile_id,p.revision profile_revision,
          COALESCE(p.id IS NOT NULL AND p.deleted_at IS NULL AND p.purge_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id),false) profile_active,
          COALESCE((SELECT jsonb_agg(jsonb_build_object('id',c.id,'publicId',c.public_id,'ownerPublicId',o.public_id,'revision',c.revision,'keyEpoch',c.key_epoch,
            'collectionActive',c.deleted_at IS NULL AND c.purge_at IS NULL AND o.state=0
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='account' AND e.resource_id=o.public_id) OR (e.resource_kind='collection' AND e.resource_id=c.public_id)),
            'grantActive',COALESCE(g.state=0 AND g.access IN(0,1) AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='grant' AND e.resource_id=g.public_id),false),
            'grantId',g.id,'grantPublicId',g.public_id,'grantRevision',g.revision,
            'grantState',g.state,'grantAccess',g.access,'ownerState',o.state,'ownerGeneration',o.revocation_generation,
            'deletedAt',c.deleted_at,'purgeAt',c.purge_at)
            ORDER BY c.public_id) FROM cerberus.profile_collection pc JOIN cerberus.collection c ON c.id=pc.collection_id
            JOIN cerberus.account o ON o.id=c.account_id LEFT JOIN cerberus.collection_grant g ON g.collection_id=c.id AND g.recipient_account_id=a.id
            WHERE pc.profile_id=p.id),'[]'::jsonb)::text related
        FROM cerberus.account a LEFT JOIN cerberus.vault_access_session s ON s.account_id=a.id AND s.handle_verifier=@verifier
        LEFT JOIN cerberus.profile p ON p.account_id=a.id AND p.public_id=@profile WHERE a.heimdall_public_id=@actor
        """;
}
