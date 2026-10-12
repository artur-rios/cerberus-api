using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace ArturRios.Cerberus.Data.Collections;

public sealed class CollectionGrantStore(IDbContextFactory<AppDbContext> factory) : ICollectionGrantStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false, NumberHandling = JsonNumberHandling.Strict };

    public async Task<VaultResult<CollectionGrantDetails>> GrantAsync(CollectionGrantRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        if (string.IsNullOrWhiteSpace(request.AccessVerifier)) return new(Error: "vault_access_required");
        if (request.CollectionId == Guid.Empty || request.Input?.IsValid() != true) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Classify the one target first. Foreign denials acquire no requested
            // recipient lock and validate only their relevant native grant.
            var initial = await SnapshotAsync(db, request, ct);
            var error = AuthorityError(initial); if (error is not null) return new(Error: error);
            if (initial.OwnerPublicId == request.Input.RecipientAccountId) return new(Error: "validation_failed");
            if (initial.RecipientId is null) return new(Error: "not_found");
            var accounts = new[] { initial.OwnerPublicId, request.Input.RecipientAccountId };
            // The INSERT also takes recipient FK KEY SHARE. Lock both accounts in
            // one ordered statement before either collection to avoid reciprocal deadlock.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE public_id=ANY({accounts}) ORDER BY public_id FOR UPDATE", ct);
            var locked = await SnapshotAsync(db, request, ct);
            error = OwnerError(locked); if (error is not null) return new(Error: error);
            if (locked.OwnerId != initial.OwnerId || locked.RecipientId != initial.RecipientId) return new(Error: "revision_conflict");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={locked.OwnerId} AND handle_verifier={request.AccessVerifier} FOR UPDATE", ct);
            var captured = await SnapshotAsync(db, request, ct);
            error = OwnerError(captured); if (error is not null) return new(Error: error);
            if (captured.Selection != initial.Selection) return new(Error: "revision_conflict");
            var item = JsonSerializer.Deserialize<Item>(captured.Item!, Json)!;
            async Task<string?> Recheck()
            {
                var current = await SnapshotAsync(db, request, ct);
                return OwnerError(current) ?? (current.Stable != captured.Stable || current.Grants != captured.Grants ? "revision_conflict" : null);
            }
            if (captured.Selection is { } profile)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE account_id={captured.OwnerId} AND id={profile} FOR NO KEY UPDATE", ct);
                error = await Recheck(); if (error is not null) return new(Error: error);
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE account_id={captured.OwnerId} AND id={item.Id} FOR UPDATE", ct);
            error = await Recheck(); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection_grant WHERE id=ANY({captured.GrantIds}) ORDER BY public_id FOR UPDATE", ct);
            error = await Recheck(); if (error is not null) return new(Error: error);
            // Parse each party's current protection once after account locks.
            var protections = await db.VaultProtections.AsNoTracking().Where(x => x.AccountId == captured.OwnerId || x.AccountId == captured.RecipientId).ToArrayAsync(ct);
            if (!CollectionGrantBinding.TryReadPins(protections.SingleOrDefault(x => x.AccountId == captured.OwnerId), out var ownerPins)
                || !CollectionGrantBinding.TryReadPins(protections.SingleOrDefault(x => x.AccountId == captured.RecipientId), out var recipientPins))
                return new(Error: "persistence_unavailable");
            var collectionEnvelope = JsonSerializer.Deserialize<EncryptedEnvelope>(Convert.FromHexString(item.Envelope), Json);
            if (collectionEnvelope?.IsValid() != true || collectionEnvelope.KeyEpoch != item.KeyEpoch || item.PurgeAt is not null
                || item.Revision is <= 0 or > ProtocolBinary.MaxInteger || item.Sequence is <= 0 or > ProtocolBinary.MaxInteger)
                return new(Error: "persistence_unavailable");
            var rows = JsonSerializer.Deserialize<GrantRow[]>(captured.Rows, Json)!;
            var pair = rows.SingleOrDefault(x => x.CollectionId == item.Id && x.RecipientId == captured.RecipientId);
            var input = request.Input;
            if (captured.Reserved || rows.Any(x => x.PublicId == input.GrantId && x != pair)
                || pair is null && input.ExpectedRevision is not null
                || pair is not null && (pair.State != (int)CollectionGrantState.Revoked || pair.PublicId != input.GrantId
                    || pair.Revision != input.ExpectedRevision || pair.Revision is <= 0 or >= ProtocolBinary.MaxInteger))
                return new(Error: "revision_conflict");
            var revision = (input.ExpectedRevision ?? 0) + 1;
            if (!input.KeyEnvelope.Verify(captured.OwnerPublicId, "collection", request.CollectionId, item.KeyEpoch,
                input.GrantId, revision, captured.RecipientIdentity!.Value, recipientPins!.RecipientKey, ownerPins!.AuthorKey))
                return new(Error: "validation_failed");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(input.KeyEnvelope, Json); var stamp = Guid.NewGuid();
            var access = input.Access == "read-only" ? CollectionGrantAccess.ReadOnly : CollectionGrantAccess.ReadWrite;
            var parameters = Parameters(request).Concat(new object[]
            {
                new NpgsqlParameter("stable", NpgsqlDbType.Jsonb) { Value = captured.Stable },
                new NpgsqlParameter("grants", NpgsqlDbType.Jsonb) { Value = captured.Grants },
                new NpgsqlParameter("replacement", bytes), new NpgsqlParameter("stamp", stamp), new NpgsqlParameter("access", (int)access),
                new NpgsqlParameter("revision", revision), new NpgsqlParameter("id", item.Id),
                new NpgsqlParameter("pair", pair?.Id ?? 0), new NpgsqlParameter("previous", pair?.Revision ?? 0)
            }).ToArray();
            // Authorization linearizes here at database time. The exact captured
            // graph prevents accepting new earlier locks or changed protection pins.
            var writeSql = Authority + (pair is null ? """
                INSERT INTO cerberus.collection_grant(public_id,collection_id,recipient_account_id,access,state,revision,recipient_key_envelope,concurrency_stamp)
                SELECT @grant,c.id,r.id,@access,@grant_active,@revision,@replacement,@stamp
                FROM target c CROSS JOIN actor a CROSS JOIN recipient r CROSS JOIN session s CROSS JOIN captured frozen
                WHERE c.id=@id AND c.account_id=a.id AND r.id<>a.id AND frozen.stable=@stable AND frozen.grants=@grants
                  AND NOT EXISTS(SELECT FROM reserved) AND NOT EXISTS(SELECT FROM grant_rows)
                ON CONFLICT DO NOTHING
                """ : """
                UPDATE cerberus.collection_grant AS g SET access=@access,state=@grant_active,revision=@revision,
                  recipient_key_envelope=@replacement,concurrency_stamp=@stamp
                FROM target c CROSS JOIN actor a CROSS JOIN recipient r CROSS JOIN session s CROSS JOIN captured frozen
                WHERE g.id=@pair AND g.collection_id=c.id AND c.id=@id AND c.account_id=a.id AND r.id<>a.id
                  AND g.recipient_account_id=r.id AND g.public_id=@grant AND g.revision=@previous AND g.state=@revoked
                  AND frozen.stable=@stable AND frozen.grants=@grants AND NOT EXISTS(SELECT FROM reserved)
                """);
            var count = await db.Database.ExecuteSqlRawAsync(writeSql, parameters, ct);
            if (count != 1) return new(Error: OwnerError(await SnapshotAsync(db, request, ct)) ?? "revision_conflict");
            // Natural expiry after authorization cannot cancel the operation.
            // This private post-write snapshot bypasses only the time predicate.
            var post = await SnapshotAsync(db, request, ct, authorized: true);
            if (post.Stable != captured.Stable || post.CorruptAncestry || post.Reserved || post.Item is null
                || !CollectionMembershipInput.ValidIds(post.RecordIds) || !CollectionMembershipInput.ValidIds(post.FolderIds))
                return new(Error: "persistence_unavailable");
            var saved = await db.CollectionGrants.AsNoTracking().SingleOrDefaultAsync(x => x.PublicId == input.GrantId, ct);
            if (saved is null || pair is not null && saved.Id != pair.Id || saved.CollectionId != item.Id || saved.RecipientAccountId != captured.RecipientId
                || saved.State != CollectionGrantState.Active || saved.Access != access || saved.Revision != revision
                || saved.ConcurrencyStamp != stamp || !saved.RecipientKeyEnvelope.AsSpan().SequenceEqual(bytes))
                return new(Error: "persistence_unavailable");
            await tx.CommitAsync(ct);
            return new(new(request.CollectionId, input.GrantId, revision, input.Access, post.RecordIds.Order().ToArray(), post.FolderIds.Order().ToArray()));
        }
        catch (PostgresException e) when (e.SqlState is "23505" or "2200H") { return new(Error: "revision_conflict"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        catch (Exception e) when (e is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            || e is InvalidOperationException { InnerException: DbException or DbUpdateException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }

    private static string? AuthorityError(Snapshot s)
    {
        if (!s.ActorActive) return "not_found";
        if (!s.Allowed) return "vault_access_denied";
        if (s.Item is null) return "not_found";
        if (!s.ForeignTarget) return null;
        var item = JsonSerializer.Deserialize<Item>(s.Item, Json)!;
        var g = s.Native is null ? null : JsonSerializer.Deserialize<Native>(s.Native, Json);
        return g is not null && CollectionGrantBinding.TryReadPins(Pins(g.OwnerMaterial, g.OwnerRevision, g.OwnerEpoch, g.OwnerGeneration), out var owner)
            && CollectionGrantBinding.TryReadPins(Pins(g.RecipientMaterial, g.RecipientRevision, g.RecipientEpoch, g.RecipientGeneration), out var recipient)
            && CollectionGrantBinding.IsBound(new() { PublicId = g.GrantId, Revision = g.Revision, RecipientKeyEnvelope = Convert.FromHexString(g.Envelope) },
                new() { PublicId = item.PublicId, KeyEpoch = item.KeyEpoch, Envelope = Convert.FromHexString(item.Envelope) }, s.OwnerPublicId, g.Identity, owner!, recipient!)
            ? "vault_access_denied" : "persistence_unavailable";
    }
    private static string? OwnerError(Snapshot s) => AuthorityError(s) ?? (s.RecipientId is null ? "not_found" : s.CorruptAncestry ? "persistence_unavailable" : null);
    private static VaultProtection? Pins(string? material, long? revision, long? epoch, long? generation) =>
        material is null || revision is null || epoch is null || generation is null ? null : new()
        { Material = Convert.FromHexString(material), Revision = revision.Value, KeyEpoch = epoch.Value, RecoveryGeneration = generation.Value };
    private static object[] Parameters(CollectionGrantRequest r, bool authorized = false) =>
    [new NpgsqlParameter("actor", r.Actor), new NpgsqlParameter("verifier", r.AccessVerifier), new NpgsqlParameter("target", r.CollectionId),
        new NpgsqlParameter("recipient", r.Input.RecipientAccountId), new NpgsqlParameter("grant", r.Input.GrantId), new NpgsqlParameter("authorized", authorized),
        new NpgsqlParameter("active", (object)(int)AccountState.Active), new NpgsqlParameter("grant_active", (object)(int)CollectionGrantState.Active),
        new NpgsqlParameter("revoked", (object)(int)CollectionGrantState.Revoked), new NpgsqlParameter("read_only", (object)(int)CollectionGrantAccess.ReadOnly),
        new NpgsqlParameter("read_write", (object)(int)CollectionGrantAccess.ReadWrite), new NpgsqlParameter("max", ProtocolBinary.MaxInteger)];
    private static async Task<Snapshot> SnapshotAsync(AppDbContext db, CollectionGrantRequest r, CancellationToken ct, bool authorized = false)
        => (await db.Database.SqlQueryRaw<Snapshot>(Authority + SnapshotSelect, Parameters(r, authorized)).ToListAsync(ct)).Single();
    private sealed record Item(long Id, Guid PublicId, long KeyEpoch, string Envelope, long Revision, long Sequence, string? PurgeAt);
    private sealed record GrantRow(long Id, Guid PublicId, long CollectionId, long RecipientId, int State, long Revision);
    private sealed record Native(Guid GrantId, long Revision, string Envelope, Guid Identity,
        string? OwnerMaterial, long? OwnerRevision, long? OwnerEpoch, long? OwnerGeneration,
        string? RecipientMaterial, long? RecipientRevision, long? RecipientEpoch, long? RecipientGeneration);
    private sealed class Snapshot
    {
        public bool ActorActive { get; set; } public bool Allowed { get; set; } public bool ForeignTarget { get; set; }
        public long OwnerId { get; set; } public Guid OwnerPublicId { get; set; } public long? RecipientId { get; set; }
        public Guid? RecipientIdentity { get; set; } public long? Selection { get; set; } public string? Item { get; set; }
        public string? Native { get; set; } public bool CorruptAncestry { get; set; } public bool Reserved { get; set; }
        public string Stable { get; set; } = "{}"; public string Grants { get; set; } = "[]"; public string Rows { get; set; } = "[]";
        public long[] GrantIds { get; set; } = []; public Guid[] RecordIds { get; set; } = []; public Guid[] FolderIds { get; set; } = [];
    }

    private const string Authority = """
        WITH RECURSIVE actor AS (
          SELECT a.* FROM cerberus.account a WHERE a.heimdall_public_id=@actor
        ), session AS (
          SELECT s.* FROM cerberus.vault_access_session s JOIN actor a ON a.id=s.account_id
          WHERE a.state=@active AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)
            AND s.handle_verifier=@verifier AND NOT s.revoked AND s.policy_revision>0 AND s.revocation_generation>0
            AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation
            AND (@authorized OR (s.issued_at<=statement_timestamp() AND ((a.renewal_enabled AND s.expires_at>statement_timestamp()) OR (NOT a.renewal_enabled AND s.expires_at IS NULL))))
            AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile p WHERE p.id=s.profile_id AND p.account_id=a.id AND p.deleted_at IS NULL
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id)))
        ), target AS MATERIALIZED (
          SELECT c.*,owner.public_id owner_public_id,g.id grant_id FROM cerberus.collection c
          JOIN cerberus.account owner ON owner.id=c.account_id CROSS JOIN session s
          LEFT JOIN cerberus.collection_grant g ON c.account_id<>s.account_id AND g.collection_id=c.id AND g.recipient_account_id=s.account_id
            AND g.state=@grant_active AND g.access IN (@read_only,@read_write) AND g.revision>0 AND g.revision<=@max
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='grant' AND e.resource_id=g.public_id)
          WHERE c.public_id=@target AND c.deleted_at IS NULL AND owner.state=@active
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='collection' AND e.resource_id=c.public_id) OR (e.resource_kind='account' AND e.resource_id=owner.public_id))
            AND (c.account_id=s.account_id OR g.id IS NOT NULL)
            AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_collection pc WHERE pc.profile_id=s.profile_id AND pc.collection_id=c.id))
        ), own_target AS (SELECT c.* FROM target c CROSS JOIN actor a WHERE c.account_id=a.id), recipient AS MATERIALIZED (
          SELECT r.* FROM cerberus.account r CROSS JOIN own_target c WHERE r.public_id=@recipient AND r.state=@active
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=r.public_id)
            AND EXISTS(SELECT FROM cerberus.vault_protection p WHERE p.account_id=r.id)
        ), grant_rows AS MATERIALIZED (
          SELECT g.* FROM cerberus.collection_grant g CROSS JOIN own_target c CROSS JOIN recipient r
          WHERE (g.collection_id=c.id AND g.recipient_account_id=r.id) OR g.public_id=@grant
        ), reserved AS (
          SELECT e.* FROM cerberus.terminal_erasure e WHERE e.resource_kind='grant'
            AND (e.resource_id=@grant OR e.resource_id IN (SELECT public_id FROM grant_rows))
        ), records AS MATERIALIZED (
          SELECT r.* FROM own_target c JOIN cerberus.collection_record cr ON cr.collection_id=c.id AND cr.account_id=c.account_id
          JOIN cerberus.record r ON r.id=cr.record_id AND r.account_id=c.account_id WHERE r.deleted_at IS NULL AND r.purge_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='record' AND e.resource_id=r.public_id)
        ), folders AS MATERIALIZED (
          SELECT f.* FROM own_target c JOIN cerberus.collection_folder cf ON cf.collection_id=c.id AND cf.account_id=c.account_id
          JOIN cerberus.folder f ON f.id=cf.folder_id AND f.account_id=c.account_id WHERE f.deleted_at IS NULL AND f.purge_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
        ), ancestry AS (
          SELECT f.id start_id,f.account_id,f.id,f.parent_folder_id,ARRAY[f.id] path,false cycle,
            (f.deleted_at IS NOT NULL OR f.purge_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)) hidden
          FROM (SELECT r.account_id,r.folder_id id FROM records r WHERE r.folder_id IS NOT NULL UNION SELECT f.account_id,f.id FROM folders f) seeds
          JOIN cerberus.folder f ON f.id=seeds.id AND f.account_id=seeds.account_id
          UNION ALL SELECT t.start_id,t.account_id,f.id,f.parent_folder_id,t.path||f.id,f.id=ANY(t.path),
            t.hidden OR f.deleted_at IS NOT NULL OR f.purge_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
          FROM ancestry t JOIN cerberus.folder f ON f.id=t.parent_folder_id AND f.account_id=t.account_id WHERE NOT t.cycle
        ), complete_ancestry AS (
          SELECT t.start_id FROM ancestry t GROUP BY t.start_id HAVING NOT bool_or(t.hidden OR t.cycle) AND bool_or(t.parent_folder_id IS NULL)
        ), captured AS (
          SELECT jsonb_build_object(
            'target',(SELECT to_jsonb(c) FROM own_target c),'actor',(SELECT to_jsonb(a) FROM actor a),'recipient',(SELECT to_jsonb(r) FROM recipient r),
            'session',(SELECT to_jsonb(s) FROM session s),
            'pins',COALESCE((SELECT jsonb_agg(to_jsonb(p) ORDER BY p.account_id) FROM cerberus.vault_protection p WHERE p.account_id IN (SELECT id FROM actor UNION SELECT id FROM recipient)),'[]'::jsonb),
            'records',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM records r),'[]'::jsonb),
            'folders',COALESCE((SELECT jsonb_agg(to_jsonb(f) ORDER BY id) FROM folders f),'[]'::jsonb),
            'ancestry',COALESCE((SELECT jsonb_agg(to_jsonb(t) ORDER BY start_id,id,cardinality(path)) FROM ancestry t),'[]'::jsonb),
            'pc',COALESCE((SELECT jsonb_agg(to_jsonb(pc) ORDER BY pc.profile_id) FROM cerberus.profile_collection pc JOIN own_target c ON c.id=pc.collection_id),'[]'::jsonb),
            'cr',COALESCE((SELECT jsonb_agg(to_jsonb(cr) ORDER BY cr.record_id) FROM cerberus.collection_record cr JOIN own_target c ON c.id=cr.collection_id),'[]'::jsonb),
            'cf',COALESCE((SELECT jsonb_agg(to_jsonb(cf) ORDER BY cf.folder_id) FROM cerberus.collection_folder cf JOIN own_target c ON c.id=cf.collection_id),'[]'::jsonb)) stable,
            COALESCE((SELECT jsonb_agg(to_jsonb(g) ORDER BY g.public_id) FROM grant_rows g),'[]'::jsonb) grants
        )
        """;
    private const string SnapshotSelect = """
        SELECT EXISTS(SELECT FROM actor a WHERE a.state=@active AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)) actor_active,
          EXISTS(SELECT FROM session) allowed,EXISTS(SELECT FROM target c CROSS JOIN actor a WHERE c.account_id<>a.id) foreign_target,
          COALESCE((SELECT account_id FROM target),0) owner_id,COALESCE((SELECT owner_public_id FROM target),'00000000-0000-0000-0000-000000000000'::uuid) owner_public_id,
          (SELECT id FROM recipient) recipient_id,(SELECT heimdall_public_id FROM recipient) recipient_identity,(SELECT profile_id FROM session) selection,
          (SELECT jsonb_build_object('id',c.id,'publicId',c.public_id,'keyEpoch',c.key_epoch,'envelope',encode(c.envelope,'hex'),
            'revision',c.revision,'sequence',c.server_sequence,'purgeAt',c.purge_at)::text FROM target c) item,
          (SELECT jsonb_build_object('grantId',g.public_id,'revision',g.revision,'envelope',encode(g.recipient_key_envelope,'hex'),'identity',a.heimdall_public_id,
            'ownerMaterial',encode(op.material,'hex'),'ownerRevision',op.revision,'ownerEpoch',op.key_epoch,'ownerGeneration',op.recovery_generation,
            'recipientMaterial',encode(rp.material,'hex'),'recipientRevision',rp.revision,'recipientEpoch',rp.key_epoch,'recipientGeneration',rp.recovery_generation)::text
            FROM target c JOIN cerberus.collection_grant g ON g.id=c.grant_id CROSS JOIN actor a
            LEFT JOIN cerberus.vault_protection op ON op.account_id=c.account_id LEFT JOIN cerberus.vault_protection rp ON rp.account_id=a.id WHERE c.account_id<>a.id) native,
          EXISTS(SELECT FROM ancestry t WHERE t.cycle AND NOT EXISTS(SELECT FROM ancestry h WHERE h.start_id=t.start_id AND h.hidden)) corrupt_ancestry,
          EXISTS(SELECT FROM reserved) reserved,frozen.stable::text stable,frozen.grants::text grants,
          COALESCE((SELECT jsonb_agg(jsonb_build_object('id',g.id,'publicId',g.public_id,'collectionId',g.collection_id,'recipientId',g.recipient_account_id,'state',g.state,'revision',g.revision) ORDER BY g.public_id) FROM grant_rows g),'[]'::jsonb)::text rows,
          ARRAY(SELECT id FROM grant_rows ORDER BY public_id) grant_ids,
          ARRAY(SELECT public_id FROM records r WHERE r.folder_id IS NULL OR EXISTS(SELECT FROM complete_ancestry t WHERE t.start_id=r.folder_id) ORDER BY public_id) record_ids,
          ARRAY(SELECT public_id FROM folders f WHERE EXISTS(SELECT FROM complete_ancestry t WHERE t.start_id=f.id) ORDER BY public_id) folder_ids
        FROM captured frozen
        """;
}
