using System.Data.Common;
using System.Globalization;
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

public sealed class CollectionMembershipStore(IDbContextFactory<AppDbContext> factory) : ICollectionMembershipStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false, NumberHandling = JsonNumberHandling.Strict };

    public async Task<VaultResult<CollectionMembershipDetails>> SetAsync(CollectionMembershipRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        if (string.IsNullOrWhiteSpace(request.AccessVerifier)) return new(Error: "vault_access_required");
        if (request.CollectionId == Guid.Empty || request.Input?.IsValid() != true) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Own-account UPDATE serializes topology writers and their new entity FK checks.
            // No foreign account or upward ancestor is explicitly locked.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR UPDATE", ct);
            var owner = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.HeimdallPublicId == request.Actor, ct);
            if (owner is null || owner.State != AccountState.Active
                || await db.TerminalErasures.AnyAsync(x => x.ResourceKind == "account" && x.ResourceId == owner.PublicId, ct)) return new(Error: "not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={owner.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE", ct);
            var initial = await SnapshotAsync(db, request, ct); var error = Error(initial);
            if (error is not null) return new(Error: error);
            var target = JsonSerializer.Deserialize<Item>(initial.Item!, Json)!;
            async Task<string?> Reload()
            {
                var current = await SnapshotAsync(db, request, ct);
                return Error(current) ?? (current.Frozen != initial.Frozen ? "revision_conflict" : null);
            }
            if (initial.Selection is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE account_id={owner.Id} AND id={initial.Selection} FOR NO KEY UPDATE", ct);
                error = await Reload(); if (error is not null) return new(Error: error);
            }
            // Collection/grant locks precede folder/record locks, matching recipient editors.
            // Capture once: a newly required member or grant requires retry, never late locks.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE account_id={owner.Id} AND id={target.Id} FOR UPDATE", ct);
            error = await Reload(); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection_grant WHERE collection_id={target.Id} AND id=ANY({initial.GrantIds}) ORDER BY public_id FOR SHARE", ct);
            error = await Reload(); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.folder WHERE account_id={owner.Id} AND id=ANY({initial.FolderIds}) ORDER BY public_id FOR UPDATE", ct);
            error = await Reload(); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.record WHERE account_id={owner.Id} AND id=ANY({initial.RecordIds}) ORDER BY public_id FOR UPDATE", ct);
            error = await Reload(); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection_folder WHERE account_id={owner.Id} AND collection_id={target.Id} ORDER BY folder_id FOR UPDATE", ct);
            error = await Reload(); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection_record WHERE account_id={owner.Id} AND collection_id={target.Id} ORDER BY record_id FOR UPDATE", ct);
            error = await Reload(); if (error is not null) return new(Error: error);

            var input = request.Input;
            if (target.Revision != input.ExpectedRevision || target.Revision == ProtocolBinary.MaxInteger || initial.Exhausted) return new(Error: "revision_conflict");
            var original = Convert.FromHexString(target.Envelope);
            var previous = JsonSerializer.Deserialize<EncryptedEnvelope>(original, Json)!;
            if (input.Envelope.KeyEpoch != target.KeyEpoch
                || input.Envelope != previous && (input.Envelope.KeySalt == previous.KeySalt || input.Envelope.Nonce == previous.Nonce)) return new(Error: "validation_failed");
            var replacement = JsonSerializer.SerializeToUtf8Bytes(input.Envelope, Json);
            var edited = input.EditedAt.AddTicks(-(input.EditedAt.Ticks % TimeSpan.TicksPerMicrosecond)); var stamp = Guid.NewGuid();
            var members = JsonSerializer.Deserialize<Member[]>(initial.Members, Json)!;
            var grants = await GrantRowsAsync(db, target.Id, ct);
            var parameters = Parameters(request).Concat(new object[]
            {
                new NpgsqlParameter("frozen", NpgsqlDbType.Jsonb) { Value = initial.Frozen },
                new NpgsqlParameter("expected", input.ExpectedRevision), new NpgsqlParameter("previous_sequence", target.Sequence),
                new NpgsqlParameter("original", original), new NpgsqlParameter("epoch", target.KeyEpoch), new NpgsqlParameter("original_stamp", target.Stamp),
                new NpgsqlParameter("owner", owner.Id), new NpgsqlParameter("id", target.Id), new NpgsqlParameter("replacement", replacement),
                new NpgsqlParameter("edited", edited), new NpgsqlParameter("stamp", stamp),
                new NpgsqlParameter("held_folders", initial.FolderIds), new NpgsqlParameter("held_records", initial.RecordIds), new NpgsqlParameter("held_grants", initial.GrantIds),
                new NpgsqlParameter("selection", NpgsqlDbType.Bigint) { Value = (object?)initial.Selection ?? DBNull.Value }
            }).ToArray();
            // Authorization linearizes here, with database time and exact graph/native evidence.
            var count = await db.Database.ExecuteSqlRawAsync(Authority + """
                UPDATE cerberus.collection AS c SET envelope=@replacement,edited_at=@edited,revision=c.revision+1,
                  server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp
                FROM target t CROSS JOIN session s CROSS JOIN captured snapshot CROSS JOIN decision d CROSS JOIN structural_state structure
                WHERE c.id=t.id AND c.id=@id AND c.account_id=@owner AND c.account_id=s.account_id AND c.public_id=@target
                  AND c.deleted_at IS NULL AND c.purge_at IS NULL AND c.revision=@expected AND c.server_sequence=@previous_sequence
                  AND c.envelope=@original AND c.key_epoch=@epoch AND c.concurrency_stamp=@original_stamp
                  AND s.profile_id IS NOT DISTINCT FROM @selection AND d.permitted AND NOT d.corrupt
                  AND NOT structure.corrupt AND NOT structure.exhausted AND snapshot.evidence=@frozen
                  AND NOT EXISTS(SELECT FROM folders f WHERE NOT(f.id=ANY(@held_folders)))
                  AND NOT EXISTS(SELECT FROM records r WHERE NOT(r.id=ANY(@held_records)))
                  AND NOT EXISTS(SELECT FROM active_grants g WHERE NOT(g.id=ANY(@held_grants)))
                """, parameters, ct);
            if (count != 1) return new(Error: Error(await SnapshotAsync(db, request, ct)) ?? "revision_conflict");
            var result = await db.Collections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == target.Id, ct);
            if (result is null || result.PublicId != request.CollectionId || result.AccountId != owner.Id || result.Revision != target.Revision + 1
                || !Safe(result.ServerSequence) || result.ServerSequence <= target.Sequence || result.EditedAt != edited || result.KeyEpoch != target.KeyEpoch
                || result.ConcurrencyStamp != stamp || !result.Envelope.AsSpan().SequenceEqual(replacement) || result.DeletedAt is not null || result.PurgeAt is not null)
                return new(Error: "persistence_unavailable");

            var targetProjection = await RowAsync(db, "collection", target.Id, ct);
            if (targetProjection is null) return new(Error: "persistence_unavailable");
            var memberProjections = new Dictionary<(string Kind, long Id), string>();
            // Count each mutation separately, so any failure rolls back the entire replacement.
            if (await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cerberus.collection_folder WHERE account_id={owner.Id} AND collection_id={target.Id}", ct) != initial.OldFolderIds.Length
                || await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cerberus.collection_record WHERE account_id={owner.Id} AND collection_id={target.Id}", ct) != initial.OldRecordIds.Length)
                return new(Error: "persistence_unavailable");
            if (await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cerberus.collection_folder(account_id,collection_id,folder_id) SELECT {owner.Id},{target.Id},f.id FROM cerberus.folder f WHERE f.account_id={owner.Id} AND f.public_id=ANY({input.FolderIds})", ct) != input.FolderIds.Length
                || await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cerberus.collection_record(account_id,collection_id,record_id) SELECT {owner.Id},{target.Id},r.id FROM cerberus.record r WHERE r.account_id={owner.Id} AND r.public_id=ANY({input.RecordIds})", ct) != input.RecordIds.Length)
                return new(Error: "persistence_unavailable");
            foreach (var member in members.Where(x => x.Changed).OrderBy(x => x.Kind == "folder" ? 0 : 1).ThenBy(x => x.PublicId))
            {
                var bump = await BumpAsync(db, member, ct); if (bump.Error is not null) return new(Error: bump.Error);
                memberProjections[(member.Kind, member.Id)] = bump.Projection!;
            }
            var actualFolders = await (from cf in db.CollectionFolders join f in db.Folders on cf.FolderId equals f.Id where cf.CollectionId == target.Id && cf.AccountId == owner.Id select f.PublicId).ToArrayAsync(ct);
            var actualRecords = await (from cr in db.CollectionRecords join r in db.Records on cr.RecordId equals r.Id where cr.CollectionId == target.Id && cr.AccountId == owner.Id select r.PublicId).ToArrayAsync(ct);
            if (!actualFolders.Order().SequenceEqual(input.FolderIds.Order()) || !actualRecords.Order().SequenceEqual(input.RecordIds.Order())
                || grants != await GrantRowsAsync(db, target.Id, ct)) return new(Error: "persistence_unavailable");
            // Verify the final projection too: a later write/trigger must not invalidate
            // the target or a member whose immediate post-write verification already passed.
            if (await RowAsync(db, "collection", target.Id, ct) != targetProjection) return new(Error: "persistence_unavailable");
            foreach (var member in members)
                if (await RowAsync(db, member.Kind, member.Id, ct) != (member.Changed ? memberProjections[(member.Kind, member.Id)] : member.Original))
                    return new(Error: "persistence_unavailable");
            await tx.CommitAsync(ct);
            return new(new(result.PublicId, result.Revision, result.ServerSequence, result.EditedAt, input.RecordIds.Order().ToArray(), input.FolderIds.Order().ToArray()));
        }
        catch (PostgresException e) when (e.SqlState == "2200H") { return new(Error: "revision_conflict"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        catch (Exception e) when (e is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            || e is InvalidOperationException { InnerException: DbException or DbUpdateException or TimeoutException }) { return new(Error: "persistence_unavailable"); }
    }

    private static bool Safe(long value) => value is > 0 and <= ProtocolBinary.MaxInteger;
    private static bool Time(string text) => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
        && time.Offset == TimeSpan.Zero && time.Ticks >= 10 && time.Ticks % 10 == 0;
    private static string? Error(Snapshot s)
    {
        if (!s.ActorActive) return "not_found";
        if (!s.Allowed) return "vault_access_denied";
        if (s.Item is null) return "not_found";
        if (s.ForeignTarget) return ValidNative(s) ? "vault_access_denied" : "persistence_unavailable";
        if (!s.Permitted) return "not_found";
        if (s.Corrupt || s.StructuralCorrupt) return "persistence_unavailable";
        var item = JsonSerializer.Deserialize<Item>(s.Item, Json)!;
        var envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(Convert.FromHexString(item.Envelope), Json);
        if (!Safe(item.Revision) || !Safe(item.Sequence) || !Safe(item.KeyEpoch) || !Time(item.EditedAt) || item.PurgeAt is not null
            || envelope?.IsValid() != true || envelope.KeyEpoch != item.KeyEpoch || !ValidNative(s)) return "persistence_unavailable";
        return null;
    }
    private static bool ValidNative(Snapshot snapshot)
    {
        var target = JsonSerializer.Deserialize<Item>(snapshot.Item!, Json)!;
        var evidence = JsonSerializer.Deserialize<GrantEvidence[]>(snapshot.Native, Json)!;
        if (evidence.Length != snapshot.GrantIds.Length) return false;
        foreach (var e in evidence)
        {
            if (e.Access is not ((int)CollectionGrantAccess.ReadOnly) and not ((int)CollectionGrantAccess.ReadWrite)
                || !CollectionGrantBinding.TryReadPins(Pins(e.OwnerMaterial, e.OwnerRevision, e.OwnerEpoch, e.OwnerGeneration), out var owner)
                || !CollectionGrantBinding.TryReadPins(Pins(e.RecipientMaterial, e.RecipientRevision, e.RecipientEpoch, e.RecipientGeneration), out var recipient)
                || !CollectionGrantBinding.IsBound(new() { PublicId = e.GrantId, Revision = e.GrantRevision, RecipientKeyEnvelope = Convert.FromHexString(e.GrantEnvelope) },
                    new() { PublicId = target.CollectionId, KeyEpoch = target.KeyEpoch, Envelope = Convert.FromHexString(target.Envelope) }, e.OwnerId, e.RecipientIdentity, owner!, recipient!)) return false;
        }
        return true;
    }
    private static VaultProtection? Pins(string? material, long? revision, long? epoch, long? generation) =>
        material is null || revision is null || epoch is null || generation is null ? null : new()
        { Material = Convert.FromHexString(material), Revision = revision.Value, KeyEpoch = epoch.Value, RecoveryGeneration = generation.Value };
    private static Task<string> GrantRowsAsync(AppDbContext db, long id, CancellationToken ct) => db.Database.SqlQuery<string>($"SELECT COALESCE(jsonb_agg(to_jsonb(g) ORDER BY g.id),'[]'::jsonb)::text AS \"Value\" FROM cerberus.collection_grant g WHERE g.collection_id={id}").SingleAsync(ct);
    private static Task<string?> RowAsync(AppDbContext db, string kind, long id, CancellationToken ct)
    {
        // kind is populated exclusively from member_rows or the private collection literal.
        var sql = $"SELECT to_jsonb(r)::text AS \"Value\" FROM cerberus.{kind} r WHERE id={{0}}";
        return db.Database.SqlQueryRaw<string>(sql, id).SingleOrDefaultAsync(ct);
    }
    private static async Task<(string? Error, string? Projection)> BumpAsync(AppDbContext db, Member member, CancellationToken ct)
    {
        var stamp = Guid.NewGuid();
        var sql = $"UPDATE cerberus.{member.Kind} SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp={{0}} WHERE id={{1}} AND account_id={{2}} AND revision={{3}} AND server_sequence={{4}} AND concurrency_stamp={{5}} AND deleted_at IS NULL AND purge_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='{member.Kind}' AND e.resource_id=cerberus.{member.Kind}.public_id)";
        if (await db.Database.ExecuteSqlRawAsync(sql, new object[] { stamp, member.Id, member.AccountId, member.Revision, member.Sequence, member.Stamp }, ct) != 1) return ("revision_conflict", null);
        var readSql = $"SELECT revision,server_sequence,concurrency_stamp,(to_jsonb(r)-'revision'-'server_sequence'-'concurrency_stamp')::text stable,to_jsonb(r)::text original FROM cerberus.{member.Kind} r WHERE id={{0}}";
        var row = await db.Database.SqlQueryRaw<Written>(readSql, member.Id).SingleOrDefaultAsync(ct);
        return row is null || row.Revision != member.Revision + 1 || !Safe(row.ServerSequence) || row.ServerSequence <= member.Sequence
            || row.ConcurrencyStamp != stamp || row.Stable != member.Stable ? ("persistence_unavailable", null) : (null, row.Original);
    }
    private sealed class Written { public long Revision { get; set; } public long ServerSequence { get; set; } public Guid ConcurrencyStamp { get; set; } public string Stable { get; set; } = ""; public string Original { get; set; } = ""; }
    private sealed record Item(Guid CollectionId, long Id, long Revision, long Sequence, string EditedAt, string Envelope, long KeyEpoch, Guid Stamp, string? PurgeAt);
    private sealed record Member(string Kind, long Id, Guid PublicId, long AccountId, long Revision, long Sequence, Guid Stamp, bool Changed, string Stable, string Original);
    private sealed record GrantEvidence(Guid GrantId, long GrantRevision, int Access, string GrantEnvelope, Guid OwnerId, Guid RecipientIdentity,
        string? OwnerMaterial, long? OwnerRevision, long? OwnerEpoch, long? OwnerGeneration,
        string? RecipientMaterial, long? RecipientRevision, long? RecipientEpoch, long? RecipientGeneration);
    private sealed class Snapshot
    {
        public bool ActorActive { get; set; } public bool Allowed { get; set; } public bool ForeignTarget { get; set; }
        public bool Permitted { get; set; } public bool Corrupt { get; set; } public bool StructuralCorrupt { get; set; } public bool Exhausted { get; set; }
        public long? Selection { get; set; } public string? Item { get; set; } public string Native { get; set; } = "[]";
        public string Members { get; set; } = "[]"; public string Frozen { get; set; } = "{}";
        public long[] FolderIds { get; set; } = []; public long[] RecordIds { get; set; } = []; public long[] GrantIds { get; set; } = [];
        public long[] OldFolderIds { get; set; } = []; public long[] OldRecordIds { get; set; } = [];
    }
    private static object[] Parameters(CollectionMembershipRequest r) =>
    [new NpgsqlParameter("actor", r.Actor), new NpgsqlParameter("verifier", r.AccessVerifier), new NpgsqlParameter("target", r.CollectionId),
        new NpgsqlParameter("folder_ids", r.Input.FolderIds), new NpgsqlParameter("record_ids", r.Input.RecordIds),
        new NpgsqlParameter("active", (object)(int)AccountState.Active), new NpgsqlParameter("grant_active", (object)(int)CollectionGrantState.Active),
        new NpgsqlParameter("read_only", (object)(int)CollectionGrantAccess.ReadOnly), new NpgsqlParameter("read_write", (object)(int)CollectionGrantAccess.ReadWrite), new NpgsqlParameter("max", ProtocolBinary.MaxInteger)];
    private static async Task<Snapshot> SnapshotAsync(AppDbContext db, CollectionMembershipRequest r, CancellationToken ct) =>
        (await db.Database.SqlQueryRaw<Snapshot>(Authority + SnapshotSelect, Parameters(r)).ToListAsync(ct)).Single();
    private const string SnapshotSelect = """
        SELECT EXISTS(SELECT FROM actor a WHERE a.state=@active AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)) actor_active,
          EXISTS(SELECT FROM session) allowed,EXISTS(SELECT FROM target c CROSS JOIN actor a WHERE c.account_id<>a.id) foreign_target,
          (SELECT profile_id FROM session) selection,
          (SELECT jsonb_build_object('collectionId',c.public_id,'id',c.id,'revision',c.revision,'sequence',c.server_sequence,'editedAt',c.edited_at,
            'envelope',encode(c.envelope,'hex'),'keyEpoch',c.key_epoch,'stamp',c.concurrency_stamp,'purgeAt',c.purge_at)::text FROM target c) item,
          d.permitted,d.corrupt,structure.corrupt structural_corrupt,structure.exhausted,n.evidence::text native,snapshot.evidence::text frozen,
          ARRAY(SELECT id FROM folders ORDER BY public_id) folder_ids,ARRAY(SELECT id FROM records ORDER BY public_id) record_ids,
          ARRAY(SELECT id FROM active_grants ORDER BY public_id) grant_ids,
          ARRAY(SELECT folder_id FROM old_folders ORDER BY folder_id) old_folder_ids,ARRAY(SELECT record_id FROM old_records ORDER BY record_id) old_record_ids,
          COALESCE((SELECT jsonb_agg(jsonb_build_object('kind',m.kind,'id',m.id,'publicId',m.public_id,'accountId',m.account_id,'revision',m.revision,
            'sequence',m.server_sequence,'stamp',m.concurrency_stamp,'changed',m.changed,'stable',m.stable,'original',m.original) ORDER BY m.kind,m.public_id) FROM member_rows m),'[]'::jsonb)::text members
        FROM decision d CROSS JOIN structural_state structure CROSS JOIN native_evidence n CROSS JOIN captured snapshot
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
        ), target AS MATERIALIZED (
          SELECT c.*,g.id grant_id FROM cerberus.collection c JOIN cerberus.account owner ON owner.id=c.account_id CROSS JOIN session s
          LEFT JOIN cerberus.collection_grant g ON c.account_id<>s.account_id AND g.collection_id=c.id AND g.recipient_account_id=s.account_id
            AND g.state=@grant_active AND g.access IN (@read_only,@read_write) AND g.revision>0 AND g.revision<=@max
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='grant' AND e.resource_id=g.public_id)
          WHERE c.public_id=@target AND c.deleted_at IS NULL AND owner.state=@active
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='collection' AND e.resource_id=c.public_id) OR (e.resource_kind='account' AND e.resource_id=owner.public_id))
            AND (c.account_id=s.account_id OR g.id IS NOT NULL)
            AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_collection pc WHERE pc.profile_id=s.profile_id AND pc.collection_id=c.id))
        ), own_target AS (SELECT c.* FROM target c CROSS JOIN actor a WHERE c.account_id=a.id),
        old_folders AS MATERIALIZED (SELECT cf.* FROM cerberus.collection_folder cf JOIN own_target c ON c.id=cf.collection_id),
        old_records AS MATERIALIZED (SELECT cr.* FROM cerberus.collection_record cr JOIN own_target c ON c.id=cr.collection_id),
        folders AS MATERIALIZED (
          SELECT f.* FROM cerberus.folder f CROSS JOIN own_target c WHERE f.account_id=c.account_id AND (f.public_id=ANY(@folder_ids) OR f.id IN (SELECT folder_id FROM old_folders))
        ), records AS MATERIALIZED (
          SELECT r.* FROM cerberus.record r CROSS JOIN own_target c WHERE r.account_id=c.account_id AND (r.public_id=ANY(@record_ids) OR r.id IN (SELECT record_id FROM old_records))
        ), requested_folders AS (
          SELECT f.* FROM folders f WHERE f.public_id=ANY(@folder_ids) AND f.deleted_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
        ), requested_records AS (
          SELECT r.* FROM records r WHERE r.public_id=ANY(@record_ids) AND r.deleted_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='record' AND e.resource_id=r.public_id)
        ), seeds AS (
          SELECT 'folder'::text kind,f.id target,f.account_id,f.id,f.public_id,f.parent_folder_id,f.deleted_at,f.purge_at FROM requested_folders f
          UNION ALL SELECT 'record',r.id,r.account_id,f.id,f.public_id,f.parent_folder_id,f.deleted_at,f.purge_at FROM requested_records r JOIN cerberus.folder f ON f.id=r.folder_id AND f.account_id=r.account_id
        ), paths AS (
          SELECT t.*,ARRAY[t.id] visited,false cycle,(t.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=t.public_id)) hidden FROM seeds t
          UNION ALL SELECT t.kind,t.target,t.account_id,f.id,f.public_id,f.parent_folder_id,f.deleted_at,f.purge_at,t.visited||f.id,f.id=ANY(t.visited),
            t.hidden OR f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
          FROM paths t JOIN cerberus.folder f ON f.id=t.parent_folder_id AND f.account_id=t.account_id WHERE NOT t.cycle
        ), folder_routes AS MATERIALIZED (
          SELECT DISTINCT t.kind,t.target,cf.collection_id,cf.folder_id FROM paths t JOIN cerberus.collection_folder cf ON cf.folder_id=t.id AND cf.account_id=t.account_id
          JOIN cerberus.collection c ON c.id=cf.collection_id AND c.account_id=t.account_id WHERE c.deleted_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='collection' AND e.resource_id=c.public_id)
        ), record_routes AS MATERIALIZED (
          SELECT cr.* FROM requested_records r JOIN cerberus.collection_record cr ON cr.record_id=r.id AND cr.account_id=r.account_id
          JOIN cerberus.collection c ON c.id=cr.collection_id AND c.account_id=r.account_id WHERE c.deleted_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='collection' AND e.resource_id=c.public_id)
        ), folder_scope AS (
          SELECT f.id,(s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN paths t ON t.id=pf.folder_id
              WHERE t.kind='folder' AND t.target=f.id AND pf.account_id=f.account_id AND pf.profile_id=s.profile_id)
            OR EXISTS(SELECT FROM folder_routes route JOIN cerberus.profile_collection pc ON pc.collection_id=route.collection_id WHERE route.kind='folder' AND route.target=f.id AND pc.profile_id=s.profile_id)) scoped
          FROM requested_folders f CROSS JOIN session s
        ), record_scope AS (
          SELECT r.id,(s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_record pr WHERE pr.account_id=r.account_id AND pr.record_id=r.id AND pr.profile_id=s.profile_id)
            OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN paths t ON t.id=pf.folder_id WHERE t.kind='record' AND t.target=r.id AND pf.account_id=r.account_id AND pf.profile_id=s.profile_id)
            OR EXISTS(SELECT FROM folder_routes route JOIN cerberus.profile_collection pc ON pc.collection_id=route.collection_id WHERE route.kind='record' AND route.target=r.id AND pc.profile_id=s.profile_id)
            OR EXISTS(SELECT FROM record_routes route JOIN cerberus.profile_collection pc ON pc.collection_id=route.collection_id WHERE route.record_id=r.id AND pc.profile_id=s.profile_id)) scoped
          FROM requested_records r CROSS JOIN session s
        ), hidden_paths AS (
          SELECT DISTINCT t.kind,t.target FROM paths t WHERE t.hidden OR (NOT t.cycle AND t.parent_folder_id IS NOT NULL AND NOT EXISTS(SELECT FROM cerberus.folder f WHERE f.id=t.parent_folder_id AND f.account_id=t.account_id))
          UNION SELECT 'record',r.id FROM requested_records r WHERE r.folder_id IS NOT NULL AND NOT EXISTS(SELECT FROM paths t WHERE t.kind='record' AND t.target=r.id)
        ), decision AS (
          SELECT (SELECT count(*) FROM requested_folders)=cardinality(@folder_ids) AND (SELECT count(*) FROM requested_records)=cardinality(@record_ids)
            AND NOT EXISTS(SELECT FROM folder_scope WHERE NOT scoped) AND NOT EXISTS(SELECT FROM record_scope WHERE NOT scoped)
            AND NOT EXISTS(SELECT FROM hidden_paths) permitted,
            EXISTS(SELECT FROM paths t WHERE t.cycle AND NOT EXISTS(SELECT FROM hidden_paths h WHERE h.kind=t.kind AND h.target=t.target)
              AND (t.kind='folder' AND EXISTS(SELECT FROM folder_scope f WHERE f.id=t.target AND f.scoped) OR t.kind='record' AND EXISTS(SELECT FROM record_scope r WHERE r.id=t.target AND r.scoped))) corrupt
        ), member_rows AS (
          SELECT 'folder'::text kind,f.id,f.public_id,f.account_id,f.revision,f.server_sequence,f.edited_at,f.purge_at,f.concurrency_stamp,
            f.deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
              AND (f.public_id=ANY(@folder_ids))<>EXISTS(SELECT FROM old_folders cf WHERE cf.folder_id=f.id) changed,
            (to_jsonb(f)-'revision'-'server_sequence'-'concurrency_stamp')::text stable,to_jsonb(f)::text original FROM folders f
          UNION ALL SELECT 'record',r.id,r.public_id,r.account_id,r.revision,r.server_sequence,r.edited_at,r.purge_at,r.concurrency_stamp,
            r.deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='record' AND e.resource_id=r.public_id)
              AND (r.public_id=ANY(@record_ids))<>EXISTS(SELECT FROM old_records cr WHERE cr.record_id=r.id),
            (to_jsonb(r)-'revision'-'server_sequence'-'concurrency_stamp')::text,to_jsonb(r)::text FROM records r
        ), structural_state AS (
          SELECT EXISTS(SELECT FROM member_rows r WHERE r.changed AND (r.revision<=0 OR r.revision>@max OR r.server_sequence<=0 OR r.server_sequence>@max
            OR NOT isfinite(r.edited_at) OR r.edited_at<'0001-01-01 00:00:00.000001+00'::timestamptz OR r.edited_at>'9999-12-31 23:59:59.999999+00'::timestamptz OR r.purge_at IS NOT NULL))
            OR EXISTS(SELECT FROM requested_folders WHERE purge_at IS NOT NULL)
            OR EXISTS(SELECT FROM requested_records WHERE purge_at IS NOT NULL)
            OR EXISTS(SELECT FROM paths WHERE purge_at IS NOT NULL)
            OR EXISTS(SELECT FROM old_folders cf CROSS JOIN actor a WHERE cf.account_id<>a.id OR NOT EXISTS(SELECT FROM folders f WHERE f.id=cf.folder_id))
            OR EXISTS(SELECT FROM old_records cr CROSS JOIN actor a WHERE cr.account_id<>a.id OR NOT EXISTS(SELECT FROM records r WHERE r.id=cr.record_id)) corrupt,
            EXISTS(SELECT FROM member_rows r WHERE r.changed AND r.revision=@max) exhausted
        ), active_grants AS MATERIALIZED (
          SELECT g.* FROM cerberus.collection_grant g JOIN target c ON c.id=g.collection_id CROSS JOIN actor a
          WHERE g.state=@grant_active AND (c.account_id=a.id OR g.id=c.grant_id)
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='grant' AND e.resource_id=g.public_id)
        ), native_evidence AS (
          SELECT COALESCE(jsonb_agg(jsonb_build_object('grantId',g.public_id,'grantRevision',g.revision,'access',g.access,'grantEnvelope',encode(g.recipient_key_envelope,'hex'),
            'ownerId',owner.public_id,'recipientIdentity',recipient.heimdall_public_id,
            'ownerMaterial',encode(op.material,'hex'),'ownerRevision',op.revision,'ownerEpoch',op.key_epoch,'ownerGeneration',op.recovery_generation,
            'recipientMaterial',encode(rp.material,'hex'),'recipientRevision',rp.revision,'recipientEpoch',rp.key_epoch,'recipientGeneration',rp.recovery_generation) ORDER BY g.public_id),'[]'::jsonb) evidence
          FROM active_grants g JOIN target c ON c.id=g.collection_id JOIN cerberus.account owner ON owner.id=c.account_id
          JOIN cerberus.account recipient ON recipient.id=g.recipient_account_id
          LEFT JOIN cerberus.vault_protection op ON op.account_id=owner.id LEFT JOIN cerberus.vault_protection rp ON rp.account_id=recipient.id
        ), captured AS (
          SELECT jsonb_build_object(
            'target',(SELECT to_jsonb(c) FROM target c),'selection',(SELECT profile_id FROM session),
            'folders',COALESCE((SELECT jsonb_agg(to_jsonb(f) ORDER BY f.public_id) FROM folders f),'[]'::jsonb),
            'records',COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.public_id) FROM records r),'[]'::jsonb),
            'paths',COALESCE((SELECT jsonb_agg(to_jsonb(t) ORDER BY t.kind,t.target,t.id,cardinality(t.visited)) FROM paths t),'[]'::jsonb),
            'oldFolders',COALESCE((SELECT jsonb_agg(to_jsonb(cf) ORDER BY cf.folder_id) FROM old_folders cf),'[]'::jsonb),
            'oldRecords',COALESCE((SELECT jsonb_agg(to_jsonb(cr) ORDER BY cr.record_id) FROM old_records cr),'[]'::jsonb),
            'PF',COALESCE((SELECT jsonb_agg(to_jsonb(pf) ORDER BY pf.profile_id,pf.folder_id) FROM cerberus.profile_folder pf CROSS JOIN session s WHERE pf.account_id=s.account_id AND pf.folder_id IN (SELECT id FROM paths)),'[]'::jsonb),
            'PR',COALESCE((SELECT jsonb_agg(to_jsonb(pr) ORDER BY pr.profile_id,pr.record_id) FROM cerberus.profile_record pr CROSS JOIN session s WHERE pr.account_id=s.account_id AND pr.record_id IN (SELECT id FROM requested_records)),'[]'::jsonb),
            'PC',COALESCE((SELECT jsonb_agg(to_jsonb(pc) ORDER BY pc.profile_id,pc.collection_id) FROM cerberus.profile_collection pc JOIN cerberus.profile p ON p.id=pc.profile_id CROSS JOIN actor a
              WHERE p.account_id=a.id AND pc.collection_id IN (SELECT id FROM target UNION SELECT collection_id FROM folder_routes UNION SELECT collection_id FROM record_routes)),'[]'::jsonb),
            'CF',COALESCE((SELECT jsonb_agg(to_jsonb(t) ORDER BY t.kind,t.target,t.collection_id,t.folder_id) FROM folder_routes t),'[]'::jsonb),
            'CR',COALESCE((SELECT jsonb_agg(to_jsonb(t) ORDER BY t.collection_id,t.record_id) FROM record_routes t),'[]'::jsonb),
            'grants',COALESCE((SELECT jsonb_agg(to_jsonb(g) ORDER BY g.public_id) FROM active_grants g),'[]'::jsonb),
            'native',(SELECT evidence FROM native_evidence)) evidence
        )
        """;
}
