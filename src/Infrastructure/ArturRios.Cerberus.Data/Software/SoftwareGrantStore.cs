using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Software;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace ArturRios.Cerberus.Data.Software;

public sealed class SoftwareGrantStore(IDbContextFactory<AppDbContext> factory) : ISoftwareGrantStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false, NumberHandling = JsonNumberHandling.Strict };

    public async Task<VaultResult<SoftwareGrantDetails>> GrantAsync(SoftwareGrantRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        if (string.IsNullOrWhiteSpace(request.AccessVerifier)) return new(Error: "vault_access_required");
        if (request.Input?.IsValid() != true) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var captured = await SnapshotAsync(db, request, ct);
            var error = AuthorityError(captured); if (error is not null) return new(Error: error);
            async Task<string?> Recheck()
            {
                var current = await SnapshotAsync(db, request, ct);
                return AuthorityError(current) ?? (current.Stable != captured.Stable || current.Grants != captured.Grants ? "revision_conflict" : null);
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={captured.OwnerId} FOR UPDATE", ct);
            var locked = await SnapshotAsync(db, request, ct);
            error = AuthorityError(locked); if (error is not null) return new(Error: error);
            // Protection writers use the same account lock. Parse these pins once.
            var protection = await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == captured.OwnerId, ct);
            if (!CollectionGrantBinding.TryReadPins(protection, out var pins)) return new(Error: "persistence_unavailable");
            if (locked.Stable != captured.Stable || locked.Grants != captured.Grants) return new(Error: "revision_conflict");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={captured.OwnerId} AND handle_verifier={request.AccessVerifier} FOR UPDATE", ct);
            error = await Recheck(); if (error is not null) return new(Error: error);
            if (captured.Selection is { } profile)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE account_id={captured.OwnerId} AND id={profile} FOR NO KEY UPDATE", ct);
                error = await Recheck(); if (error is not null) return new(Error: error);
            }
            var targets = JsonSerializer.Deserialize<Target[]>(captured.Targets, Json)!;
            // Canonical kind order agrees with native collection/member writers.
            // Sort UUIDs in PostgreSQL, independently of submitted array order.
            foreach (var kind in new[] { "collection", "record" })
            {
                var ids = targets.Where(x => x.Kind == kind).Select(x => x.PublicId).ToArray();
                if (ids.Length == 0) continue;
                var lockSql = kind == "collection"
                    ? "SELECT FROM cerberus.collection WHERE account_id={0} AND public_id=ANY({1}) ORDER BY public_id FOR UPDATE"
                    : "SELECT FROM cerberus.record WHERE account_id={0} AND public_id=ANY({1}) ORDER BY public_id FOR UPDATE";
                await db.Database.ExecuteSqlRawAsync(lockSql, [captured.OwnerId, ids], ct);
                error = await Recheck(); if (error is not null) return new(Error: error);
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.software_grant WHERE public_id={request.Input.GrantId} ORDER BY public_id FOR UPDATE", ct);
            error = await Recheck(); if (error is not null) return new(Error: error);
            var input = request.Input;
            var old = JsonSerializer.Deserialize<GrantRow[]>(captured.Rows, Json)!.SingleOrDefault();
            if (captured.Reserved || old is null && input.ExpectedRevision is not null
                || old is not null && (old.PublicId != input.GrantId || old.AccountId != captured.OwnerId
                    || old.Identity != input.SoftwareIdentityId || old.State != (int)SoftwareGrantState.Revoked
                    || old.Revision != input.ExpectedRevision || old.Revision is <= 0 or >= ProtocolBinary.MaxInteger))
                return new(Error: "revision_conflict");
            var revision = (input.ExpectedRevision ?? 0) + 1;
            foreach (var target in targets)
            {
                var envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(Convert.FromHexString(target.Envelope), Json)!;
                var supplied = input.Targets.Single(x => x.Kind == target.Kind && x.Id == target.PublicId);
                if (!supplied.KeyEnvelope.Verify(captured.OwnerPublicId, target.Kind, target.PublicId, target.KeyEpoch ?? envelope.KeyEpoch,
                    input.GrantId, revision, input.SoftwareIdentityId, input.RecipientKey.ToPublicJwk(), pins!.AuthorKey)) return new(Error: "validation_failed");
            }
            var key = JsonSerializer.SerializeToUtf8Bytes(input.RecipientKey, Json); var stamp = Guid.NewGuid();
            var parameters = Parameters(request).Concat(new object[]
            {
                new NpgsqlParameter("stable", NpgsqlDbType.Jsonb) { Value = captured.Stable },
                new NpgsqlParameter("grants", NpgsqlDbType.Jsonb) { Value = captured.Grants },
                new NpgsqlParameter("key", key), new NpgsqlParameter("stamp", stamp), new NpgsqlParameter("revision", revision),
                new NpgsqlParameter("identity", input.SoftwareIdentityId), new NpgsqlParameter("previous", old?.Revision ?? 0),
                new NpgsqlParameter("existing", old?.Id ?? 0)
            }).ToArray();
            // Database-time authorization linearizes at this guarded statement.
            var writeSql = Authority + (old is null ? """
                INSERT INTO cerberus.software_grant(public_id,account_id,heimdall_software_public_id,recipient_key,state,revision,concurrency_stamp)
                SELECT @grant,a.id,@identity,@key,0,@revision,@stamp
                FROM actor a CROSS JOIN session s CROSS JOIN captured frozen
                WHERE frozen.stable=@stable AND frozen.grants=@grants
                  AND NOT EXISTS(SELECT FROM reserved) AND NOT EXISTS(SELECT FROM grant_rows)
                ON CONFLICT DO NOTHING
                """ : """
                UPDATE cerberus.software_grant AS g SET recipient_key=@key,state=0,revision=@revision,concurrency_stamp=@stamp
                FROM actor a CROSS JOIN session s CROSS JOIN captured frozen
                WHERE g.id=@existing AND g.public_id=@grant AND g.account_id=a.id AND g.heimdall_software_public_id=@identity
                  AND g.state=1 AND g.revision=@previous AND frozen.stable=@stable AND frozen.grants=@grants
                  AND NOT EXISTS(SELECT FROM reserved)
                """);
            var count = await db.Database.ExecuteSqlRawAsync(writeSql, parameters, ct);
            if (count != 1) return new(Error: AuthorityError(await SnapshotAsync(db, request, ct)) ?? "revision_conflict");
            var saved = await db.SoftwareGrants.AsNoTracking().SingleOrDefaultAsync(x => x.PublicId == input.GrantId, ct);
            if (saved is null) return new(Error: "persistence_unavailable");
            var grantId = saved.Id;
            if (old is not null)
            {
                if (grantId != old.Id) return new(Error: "persistence_unavailable");
                using var evidence = JsonDocument.Parse(captured.Grants);
                if (await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cerberus.software_grant_record WHERE account_id={captured.OwnerId} AND grant_id={grantId}", ct)
                        != evidence.RootElement.GetProperty("records").GetArrayLength()
                    || await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cerberus.software_grant_collection WHERE account_id={captured.OwnerId} AND grant_id={grantId}", ct)
                        != evidence.RootElement.GetProperty("collections").GetArrayLength()) return new(Error: "persistence_unavailable");
            }
            foreach (var target in targets)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(input.Targets.Single(x => x.Kind == target.Kind && x.Id == target.PublicId).KeyEnvelope, Json);
                var insertSql = target.Kind == "record"
                    ? "INSERT INTO cerberus.software_grant_record(account_id,grant_id,record_id,recipient_key_envelope) VALUES({0},{1},{2},{3})"
                    : "INSERT INTO cerberus.software_grant_collection(account_id,grant_id,collection_id,recipient_key_envelope) VALUES({0},{1},{2},{3})";
                if (await db.Database.ExecuteSqlRawAsync(insertSql, [captured.OwnerId, grantId, target.Id, bytes], ct) != 1)
                    return new(Error: "persistence_unavailable");
            }
            // Only the time predicate is bypassed after authorization. Natural expiry
            // must not cancel an already authorized, still atomic aggregate.
            var post = await SnapshotAsync(db, request, ct, authorized: true);
            var records = await db.SoftwareGrantRecords.AsNoTracking().Where(x => x.GrantId == grantId).ToArrayAsync(ct);
            var collections = await db.SoftwareGrantCollections.AsNoTracking().Where(x => x.GrantId == grantId).ToArrayAsync(ct);
            saved = await db.SoftwareGrants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == grantId, ct);
            if (post.Stable != captured.Stable || post.Reserved || saved is null || saved.AccountId != captured.OwnerId
                || saved.PublicId != input.GrantId || saved.HeimdallSoftwarePublicId != input.SoftwareIdentityId
                || saved.State != SoftwareGrantState.Active || saved.Revision != revision || saved.ConcurrencyStamp != stamp
                || !saved.RecipientKey.AsSpan().SequenceEqual(key) || records.Length != targets.Count(x => x.Kind == "record")
                || collections.Length != targets.Count(x => x.Kind == "collection")) return new(Error: "persistence_unavailable");
            foreach (var target in targets)
            {
                var record = records.SingleOrDefault(x => x.RecordId == target.Id);
                var collection = collections.SingleOrDefault(x => x.CollectionId == target.Id);
                var bytes = target.Kind == "record" ? record?.RecipientKeyEnvelope : collection?.RecipientKeyEnvelope;
                var owner = target.Kind == "record" ? record?.AccountId : collection?.AccountId;
                var expected = JsonSerializer.SerializeToUtf8Bytes(input.Targets.Single(x => x.Kind == target.Kind && x.Id == target.PublicId).KeyEnvelope, Json);
                if (owner != captured.OwnerId || bytes is null || !bytes.AsSpan().SequenceEqual(expected)) return new(Error: "persistence_unavailable");
            }
            await tx.CommitAsync(ct);
            return new(new(input.GrantId, revision));
        }
        catch (Exception e) when (IsConflict(e)) { return new(Error: "revision_conflict"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        catch (Exception e) when (e is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            || e is InvalidOperationException { InnerException: DbException or DbUpdateException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }

    // Npgsql's transient errors can be wrapped by EF's execution strategy even
    // without retries. Preserve the conflict classification through those wrappers.
    private static bool IsConflict(Exception e) => e is PostgresException { SqlState: "23505" or "23514" or "40001" or "40P01" or "2200H" }
        or DbUpdateConcurrencyException || e is InvalidOperationException or DbUpdateException && e.InnerException is { } inner && IsConflict(inner);

    private static string? AuthorityError(Snapshot s)
    {
        if (!s.ActorActive) return "not_found";
        if (!s.SessionAllowed) return "vault_access_denied";
        if (s.MissingOrHiddenTarget) return "not_found";
        if (s.CorruptAncestry) return "persistence_unavailable";
        var targets = JsonSerializer.Deserialize<Target[]>(s.Targets, Json)!;
        foreach (var target in targets)
        {
            var envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(Convert.FromHexString(target.Envelope), Json);
            if (envelope?.IsValid() != true || target.KeyEpoch is { } epoch && epoch != envelope.KeyEpoch || target.PurgeAt is not null
                || target.Revision is <= 0 or > ProtocolBinary.MaxInteger || target.Sequence is <= 0 or > ProtocolBinary.MaxInteger)
                return "persistence_unavailable";
        }
        foreach (var g in JsonSerializer.Deserialize<Native[]>(s.Native, Json)!)
        {
            if (!CollectionGrantBinding.TryReadPins(Pins(g.OwnerMaterial, g.OwnerRevision, g.OwnerEpoch, g.OwnerGeneration), out var owner)
                || !CollectionGrantBinding.TryReadPins(Pins(g.RecipientMaterial, g.RecipientRevision, g.RecipientEpoch, g.RecipientGeneration), out var recipient)
                || !CollectionGrantBinding.IsBound(new() { PublicId = g.GrantId, Revision = g.Revision, RecipientKeyEnvelope = Convert.FromHexString(g.Envelope) },
                    new() { PublicId = g.CollectionId, KeyEpoch = g.CollectionEpoch, Envelope = Convert.FromHexString(g.CollectionEnvelope) },
                    g.OwnerPublicId, s.ActorIdentity, owner!, recipient!)) return "persistence_unavailable";
        }
        return targets.Any(x => x.OwnerId != s.OwnerId) ? "vault_access_denied" : null;
    }
    private static VaultProtection? Pins(string? material, long? revision, long? epoch, long? generation) =>
        material is null || revision is null || epoch is null || generation is null ? null : new()
        { Material = Convert.FromHexString(material), Revision = revision.Value, KeyEpoch = epoch.Value, RecoveryGeneration = generation.Value };
    private static object[] Parameters(SoftwareGrantRequest r, bool authorized = false) =>
    [new NpgsqlParameter("actor", r.Actor), new NpgsqlParameter("verifier", r.AccessVerifier), new NpgsqlParameter("grant", r.Input.GrantId),
        new NpgsqlParameter("requested", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(r.Input.Targets.Select(x => new { kind = x.Kind, id = x.Id }), Json) },
        new NpgsqlParameter("active", (object)(int)AccountState.Active), new NpgsqlParameter("max", ProtocolBinary.MaxInteger), new NpgsqlParameter("authorized", authorized)];
    private static async Task<Snapshot> SnapshotAsync(AppDbContext db, SoftwareGrantRequest r, CancellationToken ct, bool authorized = false) =>
        (await db.Database.SqlQueryRaw<Snapshot>(Authority + SnapshotSelect, Parameters(r, authorized)).ToListAsync(ct)).Single();
    private sealed record Target(string Kind, long Id, Guid PublicId, long OwnerId, long? KeyEpoch, string Envelope, long Revision, long Sequence, string? PurgeAt);
    private sealed record GrantRow(long Id, Guid PublicId, long AccountId, Guid Identity, int State, long Revision);
    private sealed record Native(Guid GrantId, long Revision, string Envelope, Guid CollectionId, long CollectionEpoch, string CollectionEnvelope,
        Guid OwnerPublicId, string? OwnerMaterial, long? OwnerRevision, long? OwnerEpoch, long? OwnerGeneration,
        string? RecipientMaterial, long? RecipientRevision, long? RecipientEpoch, long? RecipientGeneration);
    private sealed class Snapshot
    {
        public bool ActorActive { get; set; } public bool SessionAllowed { get; set; } public bool MissingOrHiddenTarget { get; set; }
        public bool CorruptAncestry { get; set; } public bool Reserved { get; set; }
        public long OwnerId { get; set; } public Guid OwnerPublicId { get; set; } public Guid ActorIdentity { get; set; } public long? Selection { get; set; }
        public string Targets { get; set; } = "[]"; public string Native { get; set; } = "[]"; public string Rows { get; set; } = "[]";
        public string Stable { get; set; } = "{}"; public string Grants { get; set; } = "{}";
    }

    private const string Authority = """
        WITH RECURSIVE requested AS MATERIALIZED (
          SELECT * FROM jsonb_to_recordset(@requested) AS t(kind text,id uuid)
        ), actor AS (SELECT a.* FROM cerberus.account a WHERE a.heimdall_public_id=@actor), session AS (
          SELECT s.* FROM cerberus.vault_access_session s JOIN actor a ON a.id=s.account_id
          WHERE a.state=@active AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)
            AND s.handle_verifier=@verifier AND NOT s.revoked AND s.policy_revision>0 AND s.revocation_generation>0
            AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation
            AND (@authorized OR (s.issued_at<=statement_timestamp() AND CASE WHEN a.renewal_enabled THEN s.expires_at>statement_timestamp() ELSE s.expires_at IS NULL END))
            AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile p WHERE p.id=s.profile_id AND p.account_id=a.id AND p.deleted_at IS NULL
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id)))
        ), candidates AS MATERIALIZED (
          SELECT r.* FROM cerberus.record r JOIN requested q ON q.kind='record' AND q.id=r.public_id JOIN cerberus.account a ON a.id=r.account_id
          WHERE r.deleted_at IS NULL AND a.state=@active AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e
            WHERE (e.resource_kind='record' AND e.resource_id=r.public_id) OR (e.resource_kind='account' AND e.resource_id=a.public_id))
        ), ancestry AS (
          SELECT r.id record_id,r.account_id,f.id,f.parent_folder_id,ARRAY[f.id] path,false cycle,
            (f.deleted_at IS NOT NULL OR f.purge_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)) hidden,
            to_jsonb(f) evidence
          FROM candidates r JOIN cerberus.folder f ON f.id=r.folder_id AND f.account_id=r.account_id
          UNION ALL SELECT t.record_id,t.account_id,f.id,f.parent_folder_id,t.path||f.id,f.id=ANY(t.path),
            t.hidden OR f.deleted_at IS NOT NULL OR f.purge_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id),to_jsonb(f)
          FROM ancestry t JOIN cerberus.folder f ON f.id=t.parent_folder_id AND f.account_id=t.account_id WHERE NOT t.cycle
        ), collections AS MATERIALIZED (
          SELECT c.*,g.id grant_id FROM cerberus.collection c JOIN cerberus.account a ON a.id=c.account_id CROSS JOIN session s
          LEFT JOIN cerberus.collection_grant g ON c.account_id<>s.account_id AND g.collection_id=c.id AND g.recipient_account_id=s.account_id
            AND g.state=0 AND g.access IN (0,1) AND g.revision>0 AND g.revision<=@max
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='grant' AND e.resource_id=g.public_id)
          WHERE a.state=@active AND c.deleted_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='account' AND e.resource_id=a.public_id) OR (e.resource_kind='collection' AND e.resource_id=c.public_id))
            AND (c.account_id=s.account_id OR g.id IS NOT NULL)
            AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_collection pc WHERE pc.profile_id=s.profile_id AND pc.collection_id=c.id))
            AND (EXISTS(SELECT FROM requested q WHERE q.kind='collection' AND q.id=c.public_id)
              OR EXISTS(SELECT FROM cerberus.collection_record cr JOIN candidates r ON r.id=cr.record_id AND r.account_id=cr.account_id WHERE cr.collection_id=c.id AND cr.account_id=c.account_id)
              OR EXISTS(SELECT FROM cerberus.collection_folder cf JOIN ancestry t ON t.id=cf.folder_id AND t.account_id=cf.account_id WHERE cf.collection_id=c.id AND cf.account_id=c.account_id))
        ), included AS (
          SELECT DISTINCT r.id record_id,c.id collection_id,c.grant_id FROM candidates r JOIN collections c ON c.account_id=r.account_id
          WHERE EXISTS(SELECT FROM cerberus.collection_record cr WHERE cr.collection_id=c.id AND cr.record_id=r.id AND cr.account_id=r.account_id)
            OR EXISTS(SELECT FROM cerberus.collection_folder cf JOIN ancestry t ON t.id=cf.folder_id AND t.account_id=cf.account_id
              WHERE cf.collection_id=c.id AND cf.account_id=r.account_id AND t.record_id=r.id)
        ), scoped AS MATERIALIZED (
          SELECT r.* FROM candidates r CROSS JOIN session s WHERE
            (r.account_id=s.account_id AND (s.profile_id IS NULL
              OR EXISTS(SELECT FROM cerberus.profile_record pr WHERE pr.profile_id=s.profile_id AND pr.account_id=s.account_id AND pr.record_id=r.id)
              OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN ancestry t ON t.id=pf.folder_id AND t.account_id=pf.account_id
                WHERE pf.profile_id=s.profile_id AND pf.account_id=s.account_id AND t.record_id=r.id)))
            OR EXISTS(SELECT FROM included i WHERE i.record_id=r.id)
        ), visible_records AS MATERIALIZED (
          SELECT r.* FROM scoped r WHERE NOT EXISTS(SELECT FROM ancestry t WHERE t.record_id=r.id AND (t.hidden OR t.cycle))
            AND (r.folder_id IS NULL OR EXISTS(SELECT FROM ancestry t WHERE t.record_id=r.id AND t.parent_folder_id IS NULL))
        ), targets AS MATERIALIZED (
          SELECT 'record' kind,r.id,r.public_id,r.account_id,NULL::bigint key_epoch,r.envelope,r.revision,r.server_sequence,r.purge_at,to_jsonb(r) evidence FROM visible_records r
          UNION ALL SELECT 'collection',c.id,c.public_id,c.account_id,c.key_epoch,c.envelope,c.revision,c.server_sequence,c.purge_at,to_jsonb(c)
            FROM collections c JOIN requested q ON q.kind='collection' AND q.id=c.public_id
        ), relevant_grants AS MATERIALIZED (
          SELECT DISTINCT c.grant_id FROM collections c CROSS JOIN actor a WHERE c.account_id<>a.id AND c.grant_id IS NOT NULL
            AND (EXISTS(SELECT FROM targets t WHERE t.kind='collection' AND t.id=c.id)
              OR EXISTS(SELECT FROM included i JOIN visible_records r ON r.id=i.record_id WHERE i.collection_id=c.id))
        ), grant_rows AS MATERIALIZED (SELECT g.* FROM cerberus.software_grant g WHERE g.public_id=@grant), reserved AS (
          SELECT e.* FROM cerberus.terminal_erasure e WHERE e.resource_id=@grant AND e.resource_kind='grant'
        ), captured AS (
          SELECT jsonb_build_object(
            'actor',(SELECT to_jsonb(a) FROM actor a),'session',(SELECT to_jsonb(s) FROM session s),
            'profile',(SELECT to_jsonb(p) FROM cerberus.profile p JOIN session s ON s.profile_id=p.id AND s.account_id=p.account_id),
            'pins',(SELECT to_jsonb(p) FROM cerberus.vault_protection p JOIN actor a ON a.id=p.account_id),
            'targets',COALESCE((SELECT jsonb_agg(to_jsonb(t) ORDER BY kind,public_id) FROM targets t),'[]'::jsonb),
            'ancestry',COALESCE((SELECT jsonb_agg(to_jsonb(t) ORDER BY record_id,id,cardinality(path)) FROM ancestry t),'[]'::jsonb),
            'collections',COALESCE((SELECT jsonb_agg(to_jsonb(c) ORDER BY public_id) FROM collections c),'[]'::jsonb),
            'pr',COALESCE((SELECT jsonb_agg(to_jsonb(pr) ORDER BY profile_id,record_id) FROM cerberus.profile_record pr JOIN candidates r ON r.id=pr.record_id AND r.account_id=pr.account_id),'[]'::jsonb),
            'pf',COALESCE((SELECT jsonb_agg(DISTINCT to_jsonb(pf)) FROM cerberus.profile_folder pf JOIN ancestry t ON t.id=pf.folder_id AND t.account_id=pf.account_id),'[]'::jsonb),
            'pc',COALESCE((SELECT jsonb_agg(to_jsonb(pc) ORDER BY profile_id,collection_id) FROM cerberus.profile_collection pc JOIN collections c ON c.id=pc.collection_id),'[]'::jsonb),
            'cr',COALESCE((SELECT jsonb_agg(to_jsonb(cr) ORDER BY collection_id,record_id) FROM cerberus.collection_record cr JOIN collections c ON c.id=cr.collection_id WHERE EXISTS(SELECT FROM candidates r WHERE r.id=cr.record_id AND r.account_id=cr.account_id)),'[]'::jsonb),
            'cf',COALESCE((SELECT jsonb_agg(to_jsonb(cf) ORDER BY collection_id,folder_id) FROM cerberus.collection_folder cf JOIN collections c ON c.id=cf.collection_id WHERE EXISTS(SELECT FROM ancestry t WHERE t.id=cf.folder_id AND t.account_id=cf.account_id)),'[]'::jsonb)) stable,
            jsonb_build_object('rows',COALESCE((SELECT jsonb_agg(to_jsonb(g) ORDER BY public_id) FROM grant_rows g),'[]'::jsonb),
              'records',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY record_id) FROM cerberus.software_grant_record r JOIN grant_rows g ON g.id=r.grant_id),'[]'::jsonb),
              'collections',COALESCE((SELECT jsonb_agg(to_jsonb(c) ORDER BY collection_id) FROM cerberus.software_grant_collection c JOIN grant_rows g ON g.id=c.grant_id),'[]'::jsonb)) grants
        )
        """;
    private const string SnapshotSelect = """
        SELECT EXISTS(SELECT FROM actor a WHERE a.state=@active AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)) actor_active,
          EXISTS(SELECT FROM session) session_allowed,
          (SELECT count(*) FROM requested) > (SELECT count(*) FROM targets) + (SELECT count(*) FROM scoped r
            WHERE EXISTS(SELECT FROM ancestry t WHERE t.record_id=r.id AND t.cycle) AND NOT EXISTS(SELECT FROM ancestry t WHERE t.record_id=r.id AND t.hidden)) missing_or_hidden_target,
          EXISTS(SELECT FROM scoped r JOIN ancestry t ON t.record_id=r.id WHERE t.cycle AND NOT EXISTS(SELECT FROM ancestry h WHERE h.record_id=r.id AND h.hidden)) corrupt_ancestry,
          EXISTS(SELECT FROM reserved) reserved,COALESCE((SELECT id FROM actor),0) owner_id,
          COALESCE((SELECT public_id FROM actor),'00000000-0000-0000-0000-000000000000'::uuid) owner_public_id,
          COALESCE((SELECT heimdall_public_id FROM actor),'00000000-0000-0000-0000-000000000000'::uuid) actor_identity,
          (SELECT profile_id FROM session) selection,frozen.stable::text stable,frozen.grants::text grants,
          COALESCE((SELECT jsonb_agg(jsonb_build_object('kind',t.kind,'id',t.id,'publicId',t.public_id,'ownerId',t.account_id,'keyEpoch',t.key_epoch,
            'envelope',encode(t.envelope,'hex'),'revision',t.revision,'sequence',t.server_sequence,'purgeAt',t.purge_at) ORDER BY kind,public_id) FROM targets t),'[]'::jsonb)::text targets,
          COALESCE((SELECT jsonb_agg(jsonb_build_object('id',g.id,'publicId',g.public_id,'accountId',g.account_id,'identity',g.heimdall_software_public_id,'state',g.state,'revision',g.revision) ORDER BY public_id) FROM grant_rows g),'[]'::jsonb)::text rows,
          COALESCE((SELECT jsonb_agg(jsonb_build_object('grantId',g.public_id,'revision',g.revision,'envelope',encode(g.recipient_key_envelope,'hex'),
            'collectionId',c.public_id,'collectionEpoch',c.key_epoch,'collectionEnvelope',encode(c.envelope,'hex'),'ownerPublicId',owner.public_id,
            'ownerMaterial',encode(op.material,'hex'),'ownerRevision',op.revision,'ownerEpoch',op.key_epoch,'ownerGeneration',op.recovery_generation,
            'recipientMaterial',encode(rp.material,'hex'),'recipientRevision',rp.revision,'recipientEpoch',rp.key_epoch,'recipientGeneration',rp.recovery_generation) ORDER BY g.public_id)
            FROM relevant_grants rg JOIN cerberus.collection_grant g ON g.id=rg.grant_id JOIN cerberus.collection c ON c.id=g.collection_id
            JOIN cerberus.account owner ON owner.id=c.account_id CROSS JOIN actor a LEFT JOIN cerberus.vault_protection op ON op.account_id=owner.id
            LEFT JOIN cerberus.vault_protection rp ON rp.account_id=a.id),'[]'::jsonb)::text native
        FROM captured frozen
        """;
}
