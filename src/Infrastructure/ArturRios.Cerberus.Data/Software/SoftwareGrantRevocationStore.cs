using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Software;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace ArturRios.Cerberus.Data.Software;

public sealed class SoftwareGrantRevocationStore(IDbContextFactory<AppDbContext> factory) : ISoftwareGrantRevocationStore
{
    public async Task<VaultResult<SoftwareGrantRevocationDetails>> RevokeAsync(SoftwareGrantRevocationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request is null) return new(Error: "validation_failed");
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        if (string.IsNullOrWhiteSpace(request.AccessVerifier)) return new(Error: "vault_access_required");
        if (request.Input?.IsValid(request.GrantId) != true) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var captured = await SnapshotAsync(db, request, ct);
            var error = AuthorityError(captured); if (error is not null) return new(Error: error);
            async Task<string?> Recheck() => RecheckError(await SnapshotAsync(db, request, ct), captured);

            // UC-42 uses this same lock prefix. Re-read after every possible wait.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={captured.OwnerId} FOR UPDATE", ct);
            error = await Recheck(); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={captured.OwnerId} AND handle_verifier={request.AccessVerifier} FOR UPDATE", ct);
            error = await Recheck(); if (error is not null) return new(Error: error);
            if (captured.Selection is { } profile)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE account_id={captured.OwnerId} AND id={profile} FOR NO KEY UPDATE", ct);
                error = await Recheck(); if (error is not null) return new(Error: error);
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.software_grant WHERE id={captured.GrantId} AND public_id={request.GrantId} AND account_id={captured.OwnerId} FOR UPDATE", ct);
            error = await Recheck(); if (error is not null) return new(Error: error);
            if (captured.Revision != request.Input.ExpectedRevision || captured.Revision == ProtocolBinary.MaxInteger)
                return new(Error: "revision_conflict");

            var revision = request.Input.ExpectedRevision + 1; var stamp = Guid.NewGuid();
            var parameters = Parameters(request).Concat(new object[]
            {
                new NpgsqlParameter("stable", NpgsqlDbType.Jsonb) { Value = captured.Stable },
                new NpgsqlParameter("aggregate", NpgsqlDbType.Jsonb) { Value = captured.Aggregate },
                new NpgsqlParameter("collateral", NpgsqlDbType.Jsonb) { Value = captured.Collateral },
                new NpgsqlParameter("id", captured.GrantId), new NpgsqlParameter("identity", captured.Identity),
                new NpgsqlParameter("previous", captured.Revision), new NpgsqlParameter("revision", revision),
                new NpgsqlParameter("revoked", (int)SoftwareGrantState.Revoked), new NpgsqlParameter("stamp", stamp)
            }).ToArray();
            // This statement's database clock is the authorization linearization point.
            var count = await db.Database.ExecuteSqlRawAsync(Authority + """
                UPDATE cerberus.software_grant AS g
                SET state=@revoked,revision=@revision,recipient_key=''::bytea,concurrency_stamp=@stamp
                FROM actor a CROSS JOIN session s CROSS JOIN captured frozen
                WHERE g.id=@id AND g.public_id=@grant AND g.account_id=a.id
                  AND g.heimdall_software_public_id=@identity AND g.state=@active_grant AND g.revision=@previous
                  AND frozen.stable=@stable AND frozen.aggregate=@aggregate AND frozen.collateral=@collateral
                  AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='grant' AND e.resource_id=@grant)
                """, parameters, ct);
            if (count != 1)
            {
                // A changed count after our own write is a storage fault, not a winner.
                if (count != 0 || await db.SoftwareGrants.AsNoTracking().AnyAsync(x => x.Id == captured.GrantId && x.ConcurrencyStamp == stamp, ct))
                    return new(Error: "persistence_unavailable");
                return new(Error: RecheckError(await SnapshotAsync(db, request, ct), captured) ?? "persistence_unavailable");
            }
            if (await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cerberus.software_grant_record WHERE account_id={captured.OwnerId} AND grant_id={captured.GrantId}", ct) != captured.RecordCount)
                return new(Error: "persistence_unavailable");
            if (await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cerberus.software_grant_collection WHERE account_id={captured.OwnerId} AND grant_id={captured.GrantId}", ct) != captured.CollectionCount)
                return new(Error: "persistence_unavailable");

            // Bypass only natural session time expiry after the successful CAS.
            var post = await SnapshotAsync(db, request, ct, authorized: true);
            var saved = await db.SoftwareGrants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == captured.GrantId, ct);
            if (!post.ActorActive || !post.SessionAllowed || post.RelationallyCorrupt
                || post.Stable != captured.Stable || post.Collateral != captured.Collateral
                || post.RecordCount != 0 || post.CollectionCount != 0
                || saved is null || saved.Id != captured.GrantId || saved.AccountId != captured.OwnerId
                || saved.PublicId != request.GrantId || saved.HeimdallSoftwarePublicId != captured.Identity
                || saved.State != SoftwareGrantState.Revoked || saved.Revision != revision || saved.ConcurrencyStamp != stamp
                || saved.RecipientKey.Length != 0)
                return new(Error: "persistence_unavailable");
            await tx.CommitAsync(ct);
            return new(new(request.GrantId, revision));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        // A database exception alone is no evidence of a committed concurrent winner.
        catch (Exception e) when (e is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            || e is InvalidOperationException { InnerException: DbException or DbUpdateException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }

    private static string? AuthorityError(Snapshot snapshot) => snapshot switch
    {
        { ActorActive: false } => "not_found",
        { SessionAllowed: false } => "vault_access_denied",
        { GrantVisible: false } => "not_found",
        { RelationallyCorrupt: true } => "persistence_unavailable",
        _ => null
    };
    private static string? RecheckError(Snapshot current, Snapshot captured) =>
        current.Stable != captured.Stable || current.Aggregate != captured.Aggregate || current.Collateral != captured.Collateral
            || current.GrantVisible != captured.GrantVisible || current.ActorActive != captured.ActorActive
            ? "revision_conflict" : AuthorityError(current);
    private static object[] Parameters(SoftwareGrantRevocationRequest r, bool authorized = false) =>
    [new NpgsqlParameter("actor", r.Actor), new NpgsqlParameter("verifier", r.AccessVerifier), new NpgsqlParameter("grant", r.GrantId),
        new NpgsqlParameter("active", (object)(int)AccountState.Active), new NpgsqlParameter("active_grant", (object)(int)SoftwareGrantState.Active),
        new NpgsqlParameter("max", ProtocolBinary.MaxInteger), new NpgsqlParameter("authorized", authorized)];
    private static async Task<Snapshot> SnapshotAsync(AppDbContext db, SoftwareGrantRevocationRequest r, CancellationToken ct, bool authorized = false) =>
        (await db.Database.SqlQueryRaw<Snapshot>(Authority + SnapshotSelect, Parameters(r, authorized)).ToListAsync(ct)).Single();
    private sealed class Snapshot
    {
        public bool ActorActive { get; set; } public bool SessionAllowed { get; set; } public bool GrantVisible { get; set; }
        public bool RelationallyCorrupt { get; set; } public long OwnerId { get; set; } public long? Selection { get; set; }
        public long GrantId { get; set; } public Guid Identity { get; set; } public long Revision { get; set; }
        public int RecordCount { get; set; } public int CollectionCount { get; set; }
        public string Stable { get; set; } = "{}"; public string Aggregate { get; set; } = "{}"; public string Collateral { get; set; } = "{}";
    }
    private const string Authority = """
        WITH actor AS MATERIALIZED (SELECT a.* FROM cerberus.account a WHERE a.heimdall_public_id=@actor),
        matching_session AS MATERIALIZED (
          SELECT s.* FROM cerberus.vault_access_session s JOIN actor a ON a.id=s.account_id WHERE s.handle_verifier=@verifier
        ), session AS (
          SELECT s.* FROM matching_session s JOIN actor a ON a.id=s.account_id
          WHERE a.state=@active AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)
            AND NOT s.revoked AND s.policy_revision>0 AND s.revocation_generation>0
            AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation
            AND (@authorized OR (s.issued_at<=statement_timestamp() AND CASE WHEN a.renewal_enabled THEN s.expires_at>statement_timestamp() ELSE s.expires_at IS NULL END))
            AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile p WHERE p.id=s.profile_id AND p.account_id=a.id AND p.deleted_at IS NULL
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id)))
        ), grant_rows AS MATERIALIZED (SELECT g.* FROM cerberus.software_grant g WHERE g.public_id=@grant),
        record_rows AS MATERIALIZED (SELECT r.* FROM cerberus.software_grant_record r JOIN grant_rows g ON g.id=r.grant_id),
        collection_rows AS MATERIALIZED (SELECT c.* FROM cerberus.software_grant_collection c JOIN grant_rows g ON g.id=c.grant_id),
        captured AS (
          SELECT jsonb_build_object(
            'actor',COALESCE((SELECT jsonb_agg(to_jsonb(a) ORDER BY id) FROM actor a),'[]'::jsonb),
            'session',COALESCE((SELECT jsonb_agg(to_jsonb(s) ORDER BY id) FROM matching_session s),'[]'::jsonb),
            'profile',COALESCE((SELECT jsonb_agg(to_jsonb(p) ORDER BY p.id) FROM cerberus.profile p WHERE EXISTS(SELECT FROM matching_session s WHERE s.profile_id=p.id)),'[]'::jsonb),
            'terminal',COALESCE((SELECT jsonb_agg(to_jsonb(e) ORDER BY resource_kind,resource_id) FROM cerberus.terminal_erasure e
              WHERE (e.resource_kind='grant' AND e.resource_id=@grant)
                OR (e.resource_kind='account' AND EXISTS(SELECT FROM actor a WHERE a.public_id=e.resource_id))
                OR (e.resource_kind='profile' AND EXISTS(SELECT FROM cerberus.profile p JOIN matching_session s ON s.profile_id=p.id WHERE p.public_id=e.resource_id))),'[]'::jsonb)) stable,
          jsonb_build_object(
            'rows',COALESCE((SELECT jsonb_agg(to_jsonb(g) ORDER BY id) FROM grant_rows g),'[]'::jsonb),
            'records',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY grant_id,record_id) FROM record_rows r),'[]'::jsonb),
            'collections',COALESCE((SELECT jsonb_agg(to_jsonb(c) ORDER BY grant_id,collection_id) FROM collection_rows c),'[]'::jsonb)) aggregate,
          jsonb_build_object(
            'protection',(SELECT jsonb_agg(to_jsonb(p) ORDER BY p.id) FROM cerberus.vault_protection p JOIN actor a ON a.id=p.account_id),
            'sessions',(SELECT jsonb_agg(to_jsonb(s) ORDER BY s.id) FROM cerberus.vault_access_session s JOIN actor a ON a.id=s.account_id),
            'profiles',(SELECT jsonb_agg(to_jsonb(p) ORDER BY p.id) FROM cerberus.profile p JOIN actor a ON a.id=p.account_id),
            'records',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM cerberus.record r JOIN actor a ON a.id=r.account_id),
            'collections',(SELECT jsonb_agg(to_jsonb(c) ORDER BY c.id) FROM cerberus.collection c JOIN actor a ON a.id=c.account_id),
            'folders',(SELECT jsonb_agg(to_jsonb(f) ORDER BY f.id) FROM cerberus.folder f JOIN actor a ON a.id=f.account_id),
            'pr',(SELECT jsonb_agg(to_jsonb(p) ORDER BY profile_id,record_id) FROM cerberus.profile_record p JOIN actor a ON a.id=p.account_id),
            'pf',(SELECT jsonb_agg(to_jsonb(p) ORDER BY profile_id,folder_id) FROM cerberus.profile_folder p JOIN actor a ON a.id=p.account_id),
            'pc',(SELECT jsonb_agg(to_jsonb(p) ORDER BY profile_id,collection_id) FROM cerberus.profile_collection p JOIN cerberus.profile pr ON pr.id=p.profile_id JOIN actor a ON a.id=pr.account_id),
            'cr',(SELECT jsonb_agg(to_jsonb(c) ORDER BY collection_id,record_id) FROM cerberus.collection_record c JOIN actor a ON a.id=c.account_id),
            'cf',(SELECT jsonb_agg(to_jsonb(c) ORDER BY collection_id,folder_id) FROM cerberus.collection_folder c JOIN actor a ON a.id=c.account_id),
            'native',(SELECT jsonb_agg(to_jsonb(g) ORDER BY g.id) FROM cerberus.collection_grant g JOIN cerberus.collection c ON c.id=g.collection_id JOIN actor a ON a.id=c.account_id OR a.id=g.recipient_account_id),
            'grants',(SELECT jsonb_agg(to_jsonb(g) ORDER BY g.id) FROM cerberus.software_grant g JOIN actor a ON a.id=g.account_id WHERE g.public_id<>@grant),
            'grantRecords',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r.grant_id,r.record_id) FROM cerberus.software_grant_record r JOIN actor a ON a.id=r.account_id WHERE NOT EXISTS(SELECT FROM grant_rows g WHERE g.id=r.grant_id)),
            'grantCollections',(SELECT jsonb_agg(to_jsonb(c) ORDER BY c.grant_id,c.collection_id) FROM cerberus.software_grant_collection c JOIN actor a ON a.id=c.account_id WHERE NOT EXISTS(SELECT FROM grant_rows g WHERE g.id=c.grant_id))) collateral
        )
        """;
    private const string SnapshotSelect = """
        SELECT EXISTS(SELECT FROM actor a WHERE a.state=@active AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)) actor_active,
          EXISTS(SELECT FROM session) session_allowed,
          EXISTS(SELECT FROM grant_rows g JOIN actor a ON a.id=g.account_id WHERE g.state=@active_grant
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='grant' AND e.resource_id=g.public_id)) grant_visible,
          ((SELECT count(*) FROM actor)<>1 OR (SELECT count(*) FROM matching_session)<>1 OR (SELECT count(*) FROM grant_rows)<>1
            OR EXISTS(SELECT FROM actor a WHERE a.public_id='00000000-0000-0000-0000-000000000000'::uuid
              OR a.revision<=0 OR a.revision>@max OR a.policy_revision<=0 OR a.policy_revision>@max OR a.revocation_generation<=0 OR a.revocation_generation>@max)
            OR EXISTS(SELECT FROM grant_rows g WHERE g.revision<=0 OR g.revision>@max OR g.heimdall_software_public_id='00000000-0000-0000-0000-000000000000'::uuid)
            OR EXISTS(SELECT FROM record_rows r JOIN grant_rows g ON g.id=r.grant_id WHERE r.account_id<>g.account_id
              OR NOT EXISTS(SELECT FROM cerberus.record target WHERE target.id=r.record_id AND target.account_id=g.account_id))
            OR EXISTS(SELECT FROM cerberus.software_grant_record r JOIN actor a ON a.id=r.account_id
              WHERE NOT EXISTS(SELECT FROM cerberus.software_grant g WHERE g.id=r.grant_id AND g.account_id=r.account_id))
            OR EXISTS(SELECT FROM cerberus.software_grant_collection c JOIN actor a ON a.id=c.account_id
              WHERE NOT EXISTS(SELECT FROM cerberus.software_grant g WHERE g.id=c.grant_id AND g.account_id=c.account_id))
            OR EXISTS(SELECT FROM collection_rows c JOIN grant_rows g ON g.id=c.grant_id WHERE c.account_id<>g.account_id
              OR NOT EXISTS(SELECT FROM cerberus.collection target WHERE target.id=c.collection_id AND target.account_id=g.account_id))) relationally_corrupt,
          COALESCE((SELECT min(id) FROM actor),0) owner_id,(SELECT min(profile_id) FROM matching_session) selection,
          COALESCE((SELECT min(id) FROM grant_rows),0) grant_id,
          COALESCE((SELECT heimdall_software_public_id FROM grant_rows ORDER BY id LIMIT 1),'00000000-0000-0000-0000-000000000000'::uuid) identity,
          COALESCE((SELECT min(revision) FROM grant_rows),0) revision,
          (SELECT count(*)::integer FROM record_rows) record_count,(SELECT count(*)::integer FROM collection_rows) collection_count,
          frozen.stable::text stable,frozen.aggregate::text aggregate,frozen.collateral::text collateral
        FROM captured frozen
        """;
}
