using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace ArturRios.Cerberus.Data.Collections;

public sealed class CollectionCreateStore(IDbContextFactory<AppDbContext> factory) : ICollectionCreateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<VaultResult<CollectionCreateDetails>> CreateAsync(CollectionCreateRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        if (request.Input?.IsValid() != true) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Strong own-account serialization also covers new owned-resource FKs.
            // Collections precede content to match current native recipient writers.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR UPDATE", ct);
            var owner = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.HeimdallPublicId == request.Actor, ct);
            if (owner is null || owner.State != AccountState.Active
                || await db.TerminalErasures.AnyAsync(x => x.ResourceKind == "account" && x.ResourceId == owner.PublicId, ct)) return new(Error: "not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={owner.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE", ct);
            var initial = await SnapshotAsync(db, request, ct); var error = StateError(initial);
            if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE account_id={owner.Id} AND id=ANY({initial.ProfileIds}) ORDER BY public_id FOR NO KEY UPDATE", ct);
            error = StateError(await SnapshotAsync(db, request, ct)); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE account_id={owner.Id} AND id=ANY({initial.CollectionIds}) ORDER BY public_id FOR NO KEY UPDATE", ct);
            error = StateError(await SnapshotAsync(db, request, ct)); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.folder WHERE account_id={owner.Id} AND id=ANY({initial.FolderIds}) ORDER BY public_id FOR UPDATE", ct);
            error = StateError(await SnapshotAsync(db, request, ct)); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.record WHERE account_id={owner.Id} AND id=ANY({initial.RecordIds}) ORDER BY public_id FOR UPDATE", ct);
            var current = await SnapshotAsync(db, request, ct); error = StateError(current);
            if (error is not null) return new(Error: error);
            if (current.Frozen != initial.Frozen || current.CollectionIds.Except(initial.CollectionIds).Any()) return new(Error: "revision_conflict");

            var profiles = await db.Profiles.AsNoTracking().Where(x => initial.ProfileIds.Contains(x.Id)).ToListAsync(ct);
            var folders = await db.Folders.AsNoTracking().Where(x => initial.FolderIds.Contains(x.Id)).ToListAsync(ct);
            var records = await db.Records.AsNoTracking().Where(x => initial.RecordIds.Contains(x.Id)).ToListAsync(ct);
            var changed = profiles.Select(x => new Structural("profile", x.Id, x.PublicId, x.Revision, x.ServerSequence, x.EditedAt, x.PurgeAt))
                .Concat(folders.Select(x => new Structural("folder", x.Id, x.PublicId, x.Revision, x.ServerSequence, x.EditedAt, x.PurgeAt)))
                .Concat(records.Select(x => new Structural("record", x.Id, x.PublicId, x.Revision, x.ServerSequence, x.EditedAt, x.PurgeAt))).ToArray();
            if (changed.Any(x => !Safe(x.Revision) || !Safe(x.Sequence) || x.EditedAt.Offset != TimeSpan.Zero
                || x.EditedAt.Ticks < 10 || x.EditedAt.Ticks % 10 != 0 || x.PurgeAt is not null)) return new(Error: "persistence_unavailable");
            if (changed.Any(x => x.Revision == ProtocolBinary.MaxInteger) || current.Reserved) return new(Error: "revision_conflict");
            var stable = new Dictionary<(string, long), string>();
            foreach (var item in changed) stable[(item.Kind, item.Id)] = await StableAsync(db, item, ct);

            var input = request.Input; var envelope = JsonSerializer.SerializeToUtf8Bytes(input.Envelope, Json);
            var time = input.EditedAt.AddTicks(-(input.EditedAt.Ticks % TimeSpan.TicksPerMicrosecond)); var stamp = Guid.NewGuid();
            var parameters = Parameters(request).Concat(new object[] { new NpgsqlParameter("frozen", NpgsqlDbType.Jsonb) { Value = current.Frozen },
                new NpgsqlParameter("held_profiles", initial.ProfileIds), new NpgsqlParameter("held_collections", initial.CollectionIds),
                new NpgsqlParameter("held_folders", initial.FolderIds), new NpgsqlParameter("held_records", initial.RecordIds),
                new NpgsqlParameter("envelope", envelope), new NpgsqlParameter("edited", time), new NpgsqlParameter("stamp", stamp) }).ToArray();
            // Recompute bounded authority and the whole captured inventory in the
            // statement that inserts. This is the authorization linearization point.
            var inserted = await db.Database.ExecuteSqlRawAsync(Authority + """
                INSERT INTO cerberus.collection(public_id,account_id,envelope,revision,edited_at,key_epoch,concurrency_stamp)
                SELECT @new_id,s.account_id,@envelope,1,@edited,1,@stamp FROM session s CROSS JOIN captured snapshot
                WHERE (SELECT permitted FROM decision) AND NOT (SELECT corrupt FROM decision)
                  AND NOT (SELECT structural_corrupt OR exhausted FROM structural_state)
                  AND NOT EXISTS(SELECT FROM cerberus.collection c WHERE c.public_id=@new_id)
                  AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='collection' AND e.resource_id=@new_id)
                  AND snapshot.evidence=@frozen
                  AND NOT EXISTS(SELECT FROM requested_profiles p WHERE NOT(p.id=ANY(@held_profiles)))
                  AND NOT EXISTS(SELECT FROM effective_collections c WHERE NOT(c.id=ANY(@held_collections)))
                  AND NOT EXISTS(SELECT FROM folder_candidates f WHERE NOT(f.id=ANY(@held_folders)))
                  AND NOT EXISTS(SELECT FROM record_candidates r WHERE NOT(r.id=ANY(@held_records)))
                """, parameters, ct);
            if (inserted != 1)
            {
                var after = await SnapshotAsync(db, request, ct);
                return new(Error: StateError(after) ?? "revision_conflict");
            }
            var created = await db.Collections.AsNoTracking().SingleAsync(x => x.PublicId == input.CollectionId, ct);
            if (created.AccountId != owner.Id || created.Revision != 1 || !Safe(created.ServerSequence) || created.KeyEpoch != 1
                || created.EditedAt != time || created.DeletedAt is not null || created.PurgeAt is not null
                || created.ConcurrencyStamp != stamp || !created.Envelope.AsSpan().SequenceEqual(envelope)) return new(Error: "persistence_unavailable");

            // Separate counted batches expose genuine post-mutation rollback boundaries.
            if (await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cerberus.profile_collection(profile_id,collection_id) SELECT id,{created.Id} FROM cerberus.profile WHERE account_id={owner.Id} AND id=ANY({initial.ProfileIds})", ct) != initial.ProfileIds.Length)
                return new(Error: "persistence_unavailable");
            if (await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cerberus.collection_folder(account_id,collection_id,folder_id) SELECT {owner.Id},{created.Id},id FROM cerberus.folder WHERE account_id={owner.Id} AND id=ANY({initial.FolderIds})", ct) != initial.FolderIds.Length)
                return new(Error: "persistence_unavailable");
            if (await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cerberus.collection_record(account_id,collection_id,record_id) SELECT {owner.Id},{created.Id},id FROM cerberus.record WHERE account_id={owner.Id} AND id=ANY({initial.RecordIds})", ct) != initial.RecordIds.Length)
                return new(Error: "persistence_unavailable");
            foreach (var item in changed.OrderBy(x => x.Kind == "profile" ? 0 : x.Kind == "folder" ? 1 : 2).ThenBy(x => x.PublicId))
            {
                var bumpStamp = Guid.NewGuid();
                // Kind is one of three private literals, never caller-supplied SQL.
                var updateSql = item.Kind switch
                {
                    "profile" => "UPDATE cerberus.profile SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp={0} WHERE id={1} AND account_id={2} AND revision={3} AND server_sequence={4} AND deleted_at IS NULL AND purge_at IS NULL",
                    "folder" => "UPDATE cerberus.folder SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp={0} WHERE id={1} AND account_id={2} AND revision={3} AND server_sequence={4} AND deleted_at IS NULL AND purge_at IS NULL",
                    _ => "UPDATE cerberus.record SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp={0} WHERE id={1} AND account_id={2} AND revision={3} AND server_sequence={4} AND deleted_at IS NULL AND purge_at IS NULL"
                };
                if (await db.Database.ExecuteSqlRawAsync(updateSql, [bumpStamp, item.Id, owner.Id, item.Revision, item.Sequence], ct) != 1)
                    return new(Error: "revision_conflict");
                var readSql = item.Kind switch
                {
                    "profile" => "SELECT revision,server_sequence,concurrency_stamp FROM cerberus.profile WHERE id={0}",
                    "folder" => "SELECT revision,server_sequence,concurrency_stamp FROM cerberus.folder WHERE id={0}",
                    _ => "SELECT revision,server_sequence,concurrency_stamp FROM cerberus.record WHERE id={0}"
                };
                var metadata = (await db.Database.SqlQueryRaw<Written>(readSql, item.Id).ToListAsync(ct)).Single();
                if (metadata.Revision != item.Revision + 1 || !Safe(metadata.ServerSequence) || metadata.ServerSequence <= item.Sequence
                    || metadata.ConcurrencyStamp != bumpStamp || await StableAsync(db, item, ct) != stable[(item.Kind, item.Id)]) return new(Error: "persistence_unavailable");
            }
            await tx.CommitAsync(ct);
            return new(new(created.PublicId, created.Revision, created.ServerSequence, created.EditedAt));
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UniqueViolation or "2200H") { return new(Error: "revision_conflict"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        catch (Exception e) when (e is DbException or DbUpdateException or TimeoutException
            || e is InvalidOperationException { InnerException: DbException or DbUpdateException or TimeoutException }) { return new(Error: "persistence_unavailable"); }
    }

    private static bool Safe(long n) => n is > 0 and <= ProtocolBinary.MaxInteger;
    private static string? StateError(Snapshot s) => !s.ActorActive ? "not_found" : !s.Allowed || s.SelectedEmpty ? "vault_access_denied"
        : !s.Permitted ? "not_found" : s.Corrupt || s.StructuralCorrupt ? "persistence_unavailable" : s.Exhausted ? "revision_conflict" : null;
    private static object[] Parameters(CollectionCreateRequest request) => [new NpgsqlParameter("actor", request.Actor), new NpgsqlParameter("verifier", request.AccessVerifier),
        new NpgsqlParameter("new_id", request.Input.CollectionId), new NpgsqlParameter("profile_ids", request.Input.ProfileIds),
        new NpgsqlParameter("folder_ids", request.Input.FolderIds), new NpgsqlParameter("record_ids", request.Input.RecordIds), new NpgsqlParameter("active", (object)(int)AccountState.Active), new NpgsqlParameter("max", ProtocolBinary.MaxInteger)];
    private static async Task<Snapshot> SnapshotAsync(AppDbContext db, CollectionCreateRequest request, CancellationToken ct)
        => (await db.Database.SqlQueryRaw<Snapshot>(Authority + SnapshotSelect, Parameters(request)).ToListAsync(ct)).Single();
    private static Task<string> StableAsync(AppDbContext db, Structural item, CancellationToken ct)
    {
        var sql = item.Kind switch
        {
            "profile" => "SELECT (to_jsonb(t)-'revision'-'server_sequence'-'concurrency_stamp')::text AS \"Value\" FROM cerberus.profile t WHERE t.id={0}",
            "folder" => "SELECT (to_jsonb(t)-'revision'-'server_sequence'-'concurrency_stamp')::text AS \"Value\" FROM cerberus.folder t WHERE t.id={0}",
            _ => "SELECT (to_jsonb(t)-'revision'-'server_sequence'-'concurrency_stamp')::text AS \"Value\" FROM cerberus.record t WHERE t.id={0}"
        };
        return db.Database.SqlQueryRaw<string>(sql, item.Id).SingleAsync(ct);
    }
    private sealed record Structural(string Kind, long Id, Guid PublicId, long Revision, long Sequence, DateTimeOffset EditedAt, DateTimeOffset? PurgeAt);
    private sealed class Written { public long Revision { get; set; } public long ServerSequence { get; set; } public Guid ConcurrencyStamp { get; set; } }
    private sealed class Snapshot
    {
        public bool ActorActive { get; set; } public bool Allowed { get; set; } public bool SelectedEmpty { get; set; } public bool Permitted { get; set; } public bool Corrupt { get; set; } public bool Reserved { get; set; }
        public bool StructuralCorrupt { get; set; } public bool Exhausted { get; set; }
        public long[] ProfileIds { get; set; } = []; public long[] CollectionIds { get; set; } = []; public long[] FolderIds { get; set; } = []; public long[] RecordIds { get; set; } = [];
        public string Frozen { get; set; } = "{}";
    }
    private const string SnapshotSelect = """
        SELECT EXISTS(SELECT FROM actor a WHERE a.state=@active AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)) actor_active,
          EXISTS(SELECT FROM session) allowed,EXISTS(SELECT FROM session s WHERE s.profile_id IS NOT NULL AND cardinality(@profile_ids)=0) selected_empty,
          d.permitted,d.corrupt,structure.structural_corrupt,structure.exhausted,
          (EXISTS(SELECT FROM cerberus.collection c WHERE c.public_id=@new_id) OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='collection' AND e.resource_id=@new_id)) reserved,
          ARRAY(SELECT id FROM requested_profiles ORDER BY public_id) profile_ids,ARRAY(SELECT id FROM effective_collections ORDER BY public_id) collection_ids,
          ARRAY(SELECT id FROM folder_candidates ORDER BY public_id) folder_ids,ARRAY(SELECT id FROM record_candidates ORDER BY public_id) record_ids,
          snapshot.evidence::text frozen FROM decision d CROSS JOIN captured snapshot CROSS JOIN structural_state structure
        """;
    private const string Authority = """
        WITH RECURSIVE actor AS (
          SELECT a.* FROM cerberus.account a WHERE a.heimdall_public_id=@actor
        ), session AS (
          SELECT s.* FROM cerberus.vault_access_session s JOIN actor a ON a.id=s.account_id
          WHERE a.state=@active AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)
            AND s.handle_verifier=@verifier AND NOT s.revoked AND s.policy_revision>0 AND s.revocation_generation>0
            AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation AND s.issued_at<=statement_timestamp()
            AND ((a.renewal_enabled AND s.expires_at>statement_timestamp()) OR (NOT a.renewal_enabled AND s.expires_at IS NULL))
            AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile p WHERE p.id=s.profile_id AND p.account_id=a.id AND p.deleted_at IS NULL
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id)))
        ), requested_profiles AS MATERIALIZED (
          SELECT p.* FROM cerberus.profile p JOIN actor a ON p.account_id=a.id WHERE p.public_id=ANY(@profile_ids) AND p.deleted_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id)
        ), folder_candidates AS MATERIALIZED (
          SELECT f.* FROM cerberus.folder f JOIN actor a ON f.account_id=a.id WHERE f.public_id=ANY(@folder_ids) AND f.deleted_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
        ), record_candidates AS MATERIALIZED (
          SELECT r.* FROM cerberus.record r JOIN actor a ON r.account_id=a.id WHERE r.public_id=ANY(@record_ids) AND r.deleted_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='record' AND e.resource_id=r.public_id)
        ), seeds AS (
          SELECT 'folder'::text kind,f.id target,f.account_id,f.id,f.public_id,f.parent_folder_id,f.deleted_at FROM folder_candidates f
          UNION ALL SELECT 'record',r.id,r.account_id,f.id,f.public_id,f.parent_folder_id,f.deleted_at FROM record_candidates r
            JOIN cerberus.folder f ON f.id=r.folder_id AND f.account_id=r.account_id
        ), paths AS (
          SELECT t.*,ARRAY[t.id] visited,false cycle,(t.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=t.public_id)) hidden FROM seeds t
          UNION ALL SELECT t.kind,t.target,t.account_id,f.id,f.public_id,f.parent_folder_id,f.deleted_at,t.visited||f.id,f.id=ANY(t.visited),
            t.hidden OR f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
          FROM paths t JOIN cerberus.folder f ON f.id=t.parent_folder_id AND f.account_id=t.account_id WHERE NOT t.cycle
        ), folder_routes AS MATERIALIZED (
          SELECT DISTINCT t.kind,t.target,cf.collection_id,cf.folder_id FROM paths t JOIN cerberus.collection_folder cf ON cf.folder_id=t.id AND cf.account_id=t.account_id
          JOIN cerberus.collection c ON c.id=cf.collection_id AND c.account_id=t.account_id WHERE c.deleted_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='collection' AND e.resource_id=c.public_id)
        ), record_routes AS MATERIALIZED (
          SELECT cr.* FROM record_candidates r JOIN cerberus.collection_record cr ON cr.record_id=r.id AND cr.account_id=r.account_id
          JOIN cerberus.collection c ON c.id=cr.collection_id AND c.account_id=r.account_id WHERE c.deleted_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='collection' AND e.resource_id=c.public_id)
        ), effective_collections AS MATERIALIZED (
          SELECT c.* FROM cerberus.collection c WHERE c.id IN (SELECT collection_id FROM folder_routes UNION SELECT collection_id FROM record_routes)
        ), folder_scope AS (
          SELECT f.id,(s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN paths t ON t.id=pf.folder_id
              WHERE t.kind='folder' AND t.target=f.id AND pf.account_id=f.account_id AND pf.profile_id=s.profile_id)
            OR EXISTS(SELECT FROM folder_routes route JOIN cerberus.profile_collection pc ON pc.collection_id=route.collection_id
              WHERE route.kind='folder' AND route.target=f.id AND pc.profile_id=s.profile_id)) scoped
          FROM folder_candidates f CROSS JOIN session s
        ), record_scope AS (
          SELECT r.id,(s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_record pr WHERE pr.account_id=r.account_id AND pr.record_id=r.id AND pr.profile_id=s.profile_id)
            OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN paths t ON t.id=pf.folder_id WHERE t.kind='record' AND t.target=r.id AND pf.account_id=r.account_id AND pf.profile_id=s.profile_id)
            OR EXISTS(SELECT FROM folder_routes route JOIN cerberus.profile_collection pc ON pc.collection_id=route.collection_id WHERE route.kind='record' AND route.target=r.id AND pc.profile_id=s.profile_id)
            OR EXISTS(SELECT FROM record_routes route JOIN cerberus.profile_collection pc ON pc.collection_id=route.collection_id WHERE route.record_id=r.id AND pc.profile_id=s.profile_id)) scoped
          FROM record_candidates r CROSS JOIN session s
        ), hidden_paths AS (
          SELECT DISTINCT t.kind,t.target FROM paths t WHERE t.hidden OR (NOT t.cycle AND t.parent_folder_id IS NOT NULL
            AND NOT EXISTS(SELECT FROM cerberus.folder f WHERE f.id=t.parent_folder_id AND f.account_id=t.account_id))
          UNION SELECT 'record',r.id FROM record_candidates r WHERE r.folder_id IS NOT NULL AND NOT EXISTS(SELECT FROM paths t WHERE t.kind='record' AND t.target=r.id)
        ), decision AS (
          SELECT (SELECT count(*) FROM requested_profiles)=cardinality(@profile_ids)
            AND EXISTS(SELECT FROM session s WHERE s.profile_id IS NULL OR (cardinality(@profile_ids)=1 AND EXISTS(SELECT FROM requested_profiles p WHERE p.id=s.profile_id)))
            AND (SELECT count(*) FROM folder_candidates)=cardinality(@folder_ids) AND (SELECT count(*) FROM record_candidates)=cardinality(@record_ids)
            AND NOT EXISTS(SELECT FROM folder_scope WHERE NOT scoped) AND NOT EXISTS(SELECT FROM record_scope WHERE NOT scoped)
            AND NOT EXISTS(SELECT FROM hidden_paths) permitted,
            EXISTS(SELECT FROM paths t WHERE t.cycle AND NOT EXISTS(SELECT FROM hidden_paths h WHERE h.kind=t.kind AND h.target=t.target)
              AND (t.kind='folder' AND EXISTS(SELECT FROM folder_scope f WHERE f.id=t.target AND f.scoped) OR t.kind='record' AND EXISTS(SELECT FROM record_scope r WHERE r.id=t.target AND r.scoped))) corrupt
        ), structural_rows AS (
          SELECT revision,server_sequence,edited_at,purge_at FROM requested_profiles
          UNION ALL SELECT revision,server_sequence,edited_at,purge_at FROM folder_candidates
          UNION ALL SELECT revision,server_sequence,edited_at,purge_at FROM record_candidates
        ), structural_state AS (
          SELECT EXISTS(SELECT FROM structural_rows r WHERE r.revision<=0 OR r.revision>@max OR r.server_sequence<=0 OR r.server_sequence>@max
              OR NOT isfinite(r.edited_at) OR r.edited_at<'0001-01-01 00:00:00.000001+00'::timestamptz
              OR r.edited_at>'9999-12-31 23:59:59.999999+00'::timestamptz OR r.purge_at IS NOT NULL) structural_corrupt,
            EXISTS(SELECT FROM structural_rows r WHERE r.revision=@max) exhausted
        ), captured AS (
          SELECT jsonb_build_object(
            'profiles',COALESCE((SELECT jsonb_agg(to_jsonb(p) ORDER BY p.public_id) FROM requested_profiles p),'[]'::jsonb),
            'folders',COALESCE((SELECT jsonb_agg(to_jsonb(f) ORDER BY f.public_id) FROM folder_candidates f),'[]'::jsonb),
            'records',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.public_id) FROM record_candidates r),'[]'::jsonb),
            'paths',COALESCE((SELECT jsonb_agg(to_jsonb(t) ORDER BY t.kind,t.target,t.id,cardinality(t.visited)) FROM paths t),'[]'::jsonb),
            'collections',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',c.id,'publicId',c.public_id,'accountId',c.account_id) ORDER BY c.public_id) FROM effective_collections c),'[]'::jsonb),
            'PF',COALESCE((SELECT jsonb_agg(to_jsonb(pf) ORDER BY pf.profile_id,pf.folder_id) FROM cerberus.profile_folder pf WHERE pf.account_id=(SELECT id FROM actor) AND pf.folder_id IN (SELECT id FROM paths)),'[]'::jsonb),
            'PR',COALESCE((SELECT jsonb_agg(to_jsonb(pr) ORDER BY pr.profile_id,pr.record_id) FROM cerberus.profile_record pr WHERE pr.account_id=(SELECT id FROM actor) AND pr.record_id IN (SELECT id FROM record_candidates)),'[]'::jsonb),
            'PC',COALESCE((SELECT jsonb_agg(to_jsonb(pc) ORDER BY pc.profile_id,pc.collection_id) FROM cerberus.profile_collection pc JOIN cerberus.profile p ON p.id=pc.profile_id
              WHERE p.account_id=(SELECT id FROM actor) AND pc.collection_id IN (SELECT id FROM effective_collections)),'[]'::jsonb),
            'CF',COALESCE((SELECT jsonb_agg(to_jsonb(t) ORDER BY t.kind,t.target,t.collection_id,t.folder_id) FROM folder_routes t),'[]'::jsonb),
            'CR',COALESCE((SELECT jsonb_agg(to_jsonb(t) ORDER BY t.collection_id,t.record_id) FROM record_routes t),'[]'::jsonb)) evidence
        )
        """;
}
