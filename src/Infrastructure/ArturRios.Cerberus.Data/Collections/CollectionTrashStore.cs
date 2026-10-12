using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Trash;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace ArturRios.Cerberus.Data.Collections;

public sealed class CollectionTrashStore(IDbContextFactory<AppDbContext> factory) : ICollectionTrashStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict
    };

    public async Task<VaultResult<CollectionTrashDetails>> TrashAsync(CollectionTrashRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        if (string.IsNullOrWhiteSpace(request.AccessVerifier)) return new(Error: "vault_access_required");
        if (request.CollectionId == Guid.Empty || !Safe(request.ExpectedRevision)) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Serialize same-owner topology and rotation writers, including account FK checks.
            // Foreign denial reads native evidence but acquires no foreign authority locks.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR UPDATE", ct);
            var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.HeimdallPublicId == request.Actor, ct);
            if (account is null || account.State != AccountState.Active
                || await db.TerminalErasures.AnyAsync(x => x.ResourceKind == "account" && x.ResourceId == account.PublicId, ct))
                return new(Error: "not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE", ct);
            var initial = await SnapshotAsync(db, request, ct);
            var error = Error(initial);
            if (error is not null) return new(Error: error);
            var target = JsonSerializer.Deserialize<Item>(initial.Item!, Json)!;
            var captured = await InventoryAsync(db, request, ct);
            var initialGraph = JsonSerializer.Deserialize<Inventory>(captured, Json)!;
            var profiles = initialGraph.Resources.Where(x => x.Kind == "profile" && x.AccountId == account.Id).Select(x => x.Id).ToArray();
            var folders = initialGraph.Resources.Where(x => x.Kind == "folder" && x.Active && x.AccountId == account.Id).Select(x => x.Id).ToArray();
            var records = initialGraph.Resources.Where(x => x.Kind == "record" && x.Active && x.AccountId == account.Id).Select(x => x.Id).ToArray();
            var grants = initialGraph.Grants.Select(x => x.Id).ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE account_id={account.Id} AND id=ANY({profiles}) ORDER BY public_id FOR NO KEY UPDATE", ct);
            error = Error(await SnapshotAsync(db, request, ct)); if (error is not null) return new(Error: error);
            // The target excludes new FK memberships. Collections precede recipient content locks.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE account_id={account.Id} AND id={target.Id} FOR UPDATE", ct);
            error = Error(await SnapshotAsync(db, request, ct)); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.folder WHERE account_id={account.Id} AND id=ANY({folders}) ORDER BY public_id FOR UPDATE", ct);
            error = Error(await SnapshotAsync(db, request, ct)); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.record WHERE account_id={account.Id} AND id=ANY({records}) ORDER BY public_id FOR UPDATE", ct);
            error = Error(await SnapshotAsync(db, request, ct)); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection_grant WHERE id=ANY({grants}) AND collection_id={target.Id} ORDER BY public_id FOR UPDATE", ct);
            error = Error(await SnapshotAsync(db, request, ct)); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile_collection WHERE collection_id={target.Id} ORDER BY profile_id FOR UPDATE", ct);
            error = Error(await SnapshotAsync(db, request, ct)); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection_folder WHERE collection_id={target.Id} ORDER BY folder_id FOR UPDATE", ct);
            error = Error(await SnapshotAsync(db, request, ct)); if (error is not null) return new(Error: error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection_record WHERE collection_id={target.Id} ORDER BY record_id FOR UPDATE", ct);
            var current = await SnapshotAsync(db, request, ct);
            error = Error(current); if (error is not null) return new(Error: error);
            var frozen = await InventoryAsync(db, request, ct);
            var graph = JsonSerializer.Deserialize<Inventory>(frozen, Json)!;
            // Disappearance is harmless in wide scope. Never rebuild links from the old graph.
            // Growth or replacement needs locks we did not hold; changed members preserve their winner.
            if (current.Selection != initial.Selection || current.Item != initial.Item
                || graph.Resources.Except(initialGraph.Resources).Any() || graph.Grants.Except(initialGraph.Grants).Any()
                || graph.ProfileIds.Except(initialGraph.ProfileIds).Any() || graph.FolderIds.Except(initialGraph.FolderIds).Any()
                || graph.RecordIds.Except(initialGraph.RecordIds).Any())
                return new(Error: "revision_conflict");
            if (!Safe(target.Revision) || !Safe(target.Sequence) || !Safe(target.KeyEpoch) || !Time(target.EditedAt) || target.PurgeAt is not null)
                return new(Error: "persistence_unavailable");
            var affected = graph.Resources.Where(x => x.Active && x.AccountId == account.Id
                && (x.Kind != "profile" || graph.ProfileIds.Contains(x.Id))).ToArray();
            var activeGrants = graph.Grants.Where(x => x.State == (int)CollectionGrantState.Active).ToArray();
            if (affected.Any(x => !Safe(x.Revision) || !Safe(x.Sequence) || !Time(x.EditedAt) || x.PurgeAt is not null)
                || activeGrants.Any(x => !Safe(x.Revision))) return new(Error: "persistence_unavailable");
            if (target.Revision != request.ExpectedRevision || target.Revision == ProtocolBinary.MaxInteger
                || affected.Any(x => x.Revision == ProtocolBinary.MaxInteger) || activeGrants.Any(x => x.Revision == ProtocolBinary.MaxInteger)
                || await db.TrashEntries.AnyAsync(x => x.ResourceKind == "collection" && x.ResourceId == request.CollectionId, ct))
                return new(Error: "revision_conflict");
            var snapshot = new CollectionAssociationSnapshot(
                graph.Resources.Where(x => x.Kind == "profile" && graph.ProfileIds.Contains(x.Id)).Select(x => x.PublicId).Distinct().Order().ToArray(),
                graph.Resources.Where(x => x.Kind == "record").Select(x => x.PublicId).Distinct().Order().ToArray(),
                graph.Resources.Where(x => x.Kind == "folder").Select(x => x.PublicId).Distinct().Order().ToArray());
            var original = await db.Collections.AsNoTracking().SingleAsync(x => x.Id == target.Id, ct);
            var stamp = Guid.NewGuid();
            var parameters = Parameters(request).Concat(new object[]
            {
                new NpgsqlParameter("expected", request.ExpectedRevision), new NpgsqlParameter("previous_sequence", target.Sequence),
                new NpgsqlParameter("original_stamp", target.Stamp), new NpgsqlParameter("stamp", stamp),
                new NpgsqlParameter("original", original.Envelope), new NpgsqlParameter("epoch", target.KeyEpoch),
                new NpgsqlParameter("selection", NpgsqlDbType.Bigint) { Value = (object?)initial.Selection ?? DBNull.Value },
                new NpgsqlParameter("inventory", NpgsqlDbType.Jsonb) { Value = frozen }
            }).ToArray();
            // This statement is the authorization boundary: re-evaluate current scope and inventory.
            var changed = await db.Database.ExecuteSqlRawAsync(Authority + InventoryCtes + ", payload AS (" + InventorySelect + ") " + """
                UPDATE cerberus.collection AS c SET deleted_at=statement_timestamp(),purge_at=statement_timestamp()+interval '720 hours',
                  revision=c.revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp
                FROM target t CROSS JOIN session s CROSS JOIN payload p
                WHERE c.id=t.id AND c.account_id=s.account_id AND c.public_id=@target
                  AND c.deleted_at IS NULL AND c.purge_at IS NULL AND c.revision=@expected AND c.server_sequence=@previous_sequence
                  AND c.concurrency_stamp=@original_stamp AND c.envelope=@original AND c.key_epoch=@epoch
                  AND s.profile_id IS NOT DISTINCT FROM @selection AND p.value::jsonb=@inventory
                  AND NOT EXISTS(SELECT FROM cerberus.trash_entry e WHERE e.resource_kind='collection' AND e.resource_id=c.public_id)
                """, parameters, ct);
            if (changed != 1) return new(Error: Error(await SnapshotAsync(db, request, ct)) ?? "revision_conflict");
            var row = await db.Collections.AsNoTracking().SingleAsync(x => x.Id == target.Id, ct);
            if (row.PublicId != original.PublicId || row.AccountId != original.AccountId || row.Revision != original.Revision + 1
                || !Safe(row.ServerSequence) || row.ServerSequence <= original.ServerSequence || row.ConcurrencyStamp != stamp
                || row.KeyEpoch != original.KeyEpoch || row.EditedAt != original.EditedAt || !row.Envelope.AsSpan().SequenceEqual(original.Envelope)
                || row.DeletedAt is null || !Time(row.DeletedAt.Value) || row.PurgeAt is null || !Time(row.PurgeAt.Value)
                || row.PurgeAt - row.DeletedAt != TimeSpan.FromHours(720)) return new(Error: "persistence_unavailable");
            if (await db.ProfileCollections.Where(x => x.CollectionId == target.Id).ExecuteDeleteAsync(ct) != graph.ProfileIds.Length
                || await db.CollectionFolders.Where(x => x.CollectionId == target.Id).ExecuteDeleteAsync(ct) != graph.FolderIds.Length
                || await db.CollectionRecords.Where(x => x.CollectionId == target.Id).ExecuteDeleteAsync(ct) != graph.RecordIds.Length)
                return new(Error: "persistence_unavailable");
            foreach (var resource in affected.OrderBy(x => x.Kind == "profile" ? 0 : x.Kind == "folder" ? 1 : 2).ThenBy(x => x.PublicId))
            {
                error = await BumpAsync(db, resource, ct); if (error is not null) return new(Error: error);
            }
            foreach (var grant in activeGrants)
            {
                if (await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.collection_grant SET state={(int)CollectionGrantState.Revoked},revision=revision+1 WHERE id={grant.Id} AND collection_id={target.Id} AND state={(int)CollectionGrantState.Active} AND revision={grant.Revision}", ct) != 1)
                    return new(Error: "revision_conflict");
                var updated = await db.CollectionGrants.AsNoTracking().SingleAsync(x => x.Id == grant.Id, ct);
                if (updated.State != CollectionGrantState.Revoked || updated.Revision != grant.Revision + 1
                    || await db.Database.SqlQueryRaw<string>("SELECT (to_jsonb(g)-'revision'-'state')::text AS \"Value\" FROM cerberus.collection_grant g WHERE id={0}", grant.Id).SingleAsync(ct) != grant.Stable)
                    return new(Error: "persistence_unavailable");
            }
            var operation = new TrashOperation { AccountId = account.Id, RootResourceKind = "collection", RootResourceId = row.PublicId,
                DeletedAt = row.DeletedAt.Value, PurgeAt = row.PurgeAt.Value };
            db.TrashOperations.Add(operation); await db.SaveChangesAsync(ct);
            var entry = new TrashEntry { OperationId = operation.Id, ResourceKind = "collection", ResourceId = row.PublicId,
                AssociationSnapshot = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json) };
            db.TrashEntries.Add(entry); await db.SaveChangesAsync(ct);
            var work = new RetentionWorkItem { OperationKey = "trash/" + operation.PublicId, DueAt = operation.PurgeAt };
            db.RetentionWorkItems.Add(work); await db.SaveChangesAsync(ct);
            var savedOperation = await db.TrashOperations.AsNoTracking().SingleAsync(x => x.Id == operation.Id, ct);
            var savedEntry = await db.TrashEntries.AsNoTracking().SingleAsync(x => x.Id == entry.Id, ct);
            var savedWork = await db.RetentionWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id, ct);
            if (savedOperation.PublicId != operation.PublicId || savedOperation.AccountId != account.Id || savedOperation.RootResourceKind != "collection"
                || savedOperation.RootResourceId != row.PublicId || savedOperation.DeletedAt != row.DeletedAt || savedOperation.PurgeAt != row.PurgeAt
                || savedEntry.OperationId != operation.Id || savedEntry.ResourceKind != "collection" || savedEntry.ResourceId != row.PublicId
                || !savedEntry.AssociationSnapshot.AsSpan().SequenceEqual(entry.AssociationSnapshot)
                || savedWork.OperationKey != work.OperationKey || savedWork.DueAt != row.PurgeAt) return new(Error: "persistence_unavailable");
            await tx.CommitAsync(ct);
            return new(new(row.PublicId, operation.PublicId, row.Revision, row.ServerSequence, row.DeletedAt.Value, row.PurgeAt.Value));
        }
        catch (PostgresException e) when (e.SqlState == "2200H") { return new(Error: "revision_conflict"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        catch (Exception e) when (e is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            || e is InvalidOperationException { InnerException: DbException or DbUpdateException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }

    private static string? Error(Snapshot snapshot) => StateError(snapshot)
        ?? (snapshot.ForeignTarget ? ValidNative(snapshot) ? "vault_access_denied" : "persistence_unavailable" : null);
    private static bool Time(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && Time(parsed);
    private static bool Time(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value.Ticks >= 10 && value.Ticks % 10 == 0;
    private sealed record Resource(string Kind, long Id, Guid PublicId, long AccountId, long Revision, long Sequence,
        string EditedAt, string? PurgeAt, bool Active, Guid Stamp, string Stable);
    private sealed record Grant(long Id, Guid PublicId, long Revision, int State, string Stable);
    private sealed record Inventory(Resource[] Resources, Grant[] Grants, long[] ProfileIds, long[] FolderIds, long[] RecordIds);
    private sealed class InventoryRow { public string Value { get; set; } = ""; }
    private sealed class Written { public long Revision { get; set; } public long ServerSequence { get; set; } public Guid ConcurrencyStamp { get; set; } }
    private static async Task<string> InventoryAsync(AppDbContext db, CollectionTrashRequest request, CancellationToken ct)
        => (await db.Database.SqlQueryRaw<InventoryRow>(Authority + InventoryCtes + InventorySelect, Parameters(request)).ToListAsync(ct)).Single().Value;
    private static async Task<string?> BumpAsync(AppDbContext db, Resource resource, CancellationToken ct)
    {
        // Kind comes only from the three private SQL literals in InventoryCtes.
        var stamp = Guid.NewGuid();
        var sql = $"UPDATE cerberus.{resource.Kind} SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp={{0}} WHERE id={{1}} AND account_id={{2}} AND revision={{3}} AND server_sequence={{4}} AND concurrency_stamp={{5}} AND deleted_at IS NULL AND purge_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='{resource.Kind}' AND e.resource_id=cerberus.{resource.Kind}.public_id)";
        if (await db.Database.ExecuteSqlRawAsync(sql, new object[] { stamp, resource.Id, resource.AccountId, resource.Revision, resource.Sequence, resource.Stamp }, ct) != 1)
            return "revision_conflict";
        var readSql = $"SELECT revision,server_sequence,concurrency_stamp FROM cerberus.{resource.Kind} WHERE id={{0}}";
        var stableSql = $"SELECT (to_jsonb(r)-'revision'-'server_sequence'-'concurrency_stamp')::text AS \"Value\" FROM cerberus.{resource.Kind} r WHERE id={{0}}";
        var current = await db.Database.SqlQueryRaw<Written>(readSql, resource.Id).SingleAsync(ct);
        var stable = await db.Database.SqlQueryRaw<string>(stableSql, resource.Id).SingleAsync(ct);
        return current.Revision != resource.Revision + 1 || !Safe(current.ServerSequence) || current.ServerSequence <= resource.Sequence
            || current.ConcurrencyStamp != stamp || stable != resource.Stable ? "persistence_unavailable" : null;
    }

    private const string InventoryCtes = """
        , profile_links AS MATERIALIZED (SELECT pc.profile_id FROM cerberus.profile_collection pc JOIN target c ON c.id=pc.collection_id),
        folder_links AS MATERIALIZED (SELECT cf.folder_id FROM cerberus.collection_folder cf JOIN target c ON c.id=cf.collection_id),
        record_links AS MATERIALIZED (SELECT cr.record_id FROM cerberus.collection_record cr JOIN target c ON c.id=cr.collection_id),
        resources AS (
          SELECT 'profile'::text kind,p.id,p.public_id,p.account_id,
            CASE WHEN p.account_id=s.account_id THEN p.revision ELSE 1 END revision,
            CASE WHEN p.account_id=s.account_id THEN p.server_sequence ELSE 1 END server_sequence,
            CASE WHEN p.account_id=s.account_id THEN p.edited_at ELSE '1970-01-01'::timestamptz END edited_at,
            CASE WHEN p.account_id=s.account_id THEN p.purge_at ELSE NULL END purge_at,
            p.account_id=s.account_id AND p.deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id) active,
            CASE WHEN p.account_id=s.account_id THEN p.concurrency_stamp ELSE '00000000-0000-0000-0000-000000000000'::uuid END stamp,
            CASE WHEN p.account_id=s.account_id THEN (to_jsonb(p)-'revision'-'server_sequence'-'concurrency_stamp')::text ELSE jsonb_build_object()::text END stable
          FROM cerberus.profile p CROSS JOIN session s WHERE p.id IN (SELECT profile_id FROM profile_links) OR p.id=s.profile_id
          UNION ALL
          SELECT 'folder',f.id,f.public_id,f.account_id,f.revision,f.server_sequence,f.edited_at,f.purge_at,
            f.deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id),
            f.concurrency_stamp,(to_jsonb(f)-'revision'-'server_sequence'-'concurrency_stamp')::text
          FROM cerberus.folder f WHERE f.id IN (SELECT folder_id FROM folder_links)
          UNION ALL
          SELECT 'record',r.id,r.public_id,r.account_id,r.revision,r.server_sequence,r.edited_at,r.purge_at,
            r.deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='record' AND e.resource_id=r.public_id),
            r.concurrency_stamp,(to_jsonb(r)-'revision'-'server_sequence'-'concurrency_stamp')::text
          FROM cerberus.record r WHERE r.id IN (SELECT record_id FROM record_links)
        )
        """;
    private const string InventorySelect = """
        SELECT jsonb_build_object(
          'resources',COALESCE((SELECT jsonb_agg(jsonb_build_object('kind',r.kind,'id',r.id,'publicId',r.public_id,'accountId',r.account_id,
            'revision',r.revision,'sequence',r.server_sequence,'editedAt',r.edited_at,'purgeAt',r.purge_at,'active',r.active,'stamp',r.stamp,'stable',r.stable) ORDER BY r.kind,r.public_id) FROM resources r),'[]'::jsonb),
          'grants',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',g.id,'publicId',g.public_id,'revision',g.revision,'state',g.state,
            'stable',(to_jsonb(g)-'revision'-'state')::text) ORDER BY g.public_id) FROM cerberus.collection_grant g JOIN target c ON c.id=g.collection_id),'[]'::jsonb),
          'profileIds',ARRAY(SELECT profile_id FROM profile_links ORDER BY profile_id),
          'folderIds',ARRAY(SELECT folder_id FROM folder_links ORDER BY folder_id),
          'recordIds',ARRAY(SELECT record_id FROM record_links ORDER BY record_id))::text AS value
        """;

    private static bool Safe(long value) => value is > 0 and <= ProtocolBinary.MaxInteger;
    private static string? StateError(Snapshot snapshot) => !snapshot.ActorActive ? "not_found"
        : !snapshot.Allowed ? "vault_access_denied" : snapshot.Item is null ? "not_found" : null;

    private static bool ValidNative(Snapshot snapshot)
    {
        var item = JsonSerializer.Deserialize<Item>(snapshot.Item!, Json)!;
        var grant = snapshot.Grant is null ? null : JsonSerializer.Deserialize<GrantEvidence>(snapshot.Grant, Json);
        return grant is not null
            && CollectionGrantBinding.TryReadPins(Pins(grant.OwnerMaterial, grant.OwnerRevision, grant.OwnerEpoch, grant.OwnerGeneration), out var owner)
            && CollectionGrantBinding.TryReadPins(Pins(grant.RecipientMaterial, grant.RecipientRevision, grant.RecipientEpoch, grant.RecipientGeneration), out var recipient)
            && CollectionGrantBinding.IsBound(
                new() { PublicId = grant.GrantId, Revision = grant.GrantRevision, RecipientKeyEnvelope = Convert.FromHexString(grant.GrantEnvelope) },
                new() { PublicId = item.CollectionId, KeyEpoch = item.KeyEpoch, Envelope = Convert.FromHexString(item.Envelope) },
                grant.OwnerId, grant.RecipientIdentity, owner!, recipient!);
    }

    private static VaultProtection? Pins(string? material, long? revision, long? epoch, long? generation) =>
        material is null || revision is null || epoch is null || generation is null ? null : new()
        { Material = Convert.FromHexString(material), Revision = revision.Value, KeyEpoch = epoch.Value, RecoveryGeneration = generation.Value };

    private static object[] Parameters(CollectionTrashRequest request) =>
    [
        new NpgsqlParameter("actor", request.Actor), new NpgsqlParameter("verifier", request.AccessVerifier),
        new NpgsqlParameter("target", request.CollectionId), new NpgsqlParameter("active", (object)(int)AccountState.Active),
        new NpgsqlParameter("grant_active", (object)(int)CollectionGrantState.Active),
        new NpgsqlParameter("read_only", (object)(int)CollectionGrantAccess.ReadOnly),
        new NpgsqlParameter("read_write", (object)(int)CollectionGrantAccess.ReadWrite), new NpgsqlParameter("max", ProtocolBinary.MaxInteger)
    ];

    private static async Task<Snapshot> SnapshotAsync(AppDbContext db, CollectionTrashRequest request, CancellationToken ct) =>
        (await db.Database.SqlQueryRaw<Snapshot>(Authority + SnapshotSelect, Parameters(request)).ToListAsync(ct)).Single();

    private sealed record Item(Guid CollectionId, long Id, long AccountId, long Revision, long Sequence,
        string EditedAt, string Envelope, long KeyEpoch, Guid Stamp, DateTimeOffset? PurgeAt);
    private sealed record GrantEvidence(Guid GrantId, long GrantRevision, string GrantEnvelope, Guid OwnerId, Guid RecipientIdentity,
        string? OwnerMaterial, long? OwnerRevision, long? OwnerEpoch, long? OwnerGeneration,
        string? RecipientMaterial, long? RecipientRevision, long? RecipientEpoch, long? RecipientGeneration);
    private sealed class Snapshot
    {
        public bool ActorActive { get; set; }
        public bool Allowed { get; set; }
        public bool ForeignTarget { get; set; }
        public long? Selection { get; set; }
        public string? Item { get; set; }
        public string? Grant { get; set; }
    }

    private const string Authority = """
        WITH actor AS (
            SELECT a.* FROM cerberus.account a WHERE a.heimdall_public_id=@actor
        ), session AS (
            SELECT s.* FROM cerberus.vault_access_session s JOIN actor a ON a.id=s.account_id
            WHERE a.state=@active
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)
              AND s.handle_verifier=@verifier AND NOT s.revoked
              AND s.policy_revision>0 AND s.revocation_generation>0
              AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation
              AND s.issued_at<=statement_timestamp()
              AND CASE WHEN a.renewal_enabled THEN s.expires_at>statement_timestamp() ELSE s.expires_at IS NULL END
              AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile p
                WHERE p.id=s.profile_id AND p.account_id=a.id AND p.deleted_at IS NULL
                  AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id)))
        ), target AS MATERIALIZED (
            SELECT c.*,g.id grant_id FROM cerberus.collection c
            JOIN cerberus.account owner ON owner.id=c.account_id CROSS JOIN session s
            LEFT JOIN cerberus.collection_grant g ON c.account_id<>s.account_id
              AND g.collection_id=c.id AND g.recipient_account_id=s.account_id
              AND g.state=@grant_active AND g.access IN (@read_only,@read_write)
              AND g.revision>0 AND g.revision<=@max
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='grant' AND e.resource_id=g.public_id)
            WHERE c.public_id=@target AND c.deleted_at IS NULL AND owner.state=@active
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e
                WHERE (e.resource_kind='collection' AND e.resource_id=c.public_id) OR (e.resource_kind='account' AND e.resource_id=owner.public_id))
              AND (c.account_id=s.account_id OR g.id IS NOT NULL)
              AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_collection pc WHERE pc.profile_id=s.profile_id AND pc.collection_id=c.id))
        )
        """;

    private const string SnapshotSelect = """
        SELECT EXISTS(SELECT FROM actor a WHERE a.state=@active
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)) AS actor_active,
          EXISTS(SELECT FROM session) AS allowed,
          EXISTS(SELECT FROM target c CROSS JOIN actor a WHERE c.account_id<>a.id) AS foreign_target,
          (SELECT s.profile_id FROM session s) AS selection,
          (SELECT jsonb_build_object('collectionId',c.public_id,'id',c.id,'accountId',c.account_id,'revision',c.revision,
            'sequence',c.server_sequence,'editedAt',c.edited_at,'envelope',encode(c.envelope,'hex'),'keyEpoch',c.key_epoch,
            'stamp',c.concurrency_stamp,'purgeAt',c.purge_at)::text FROM target c) AS item,
          (SELECT jsonb_build_object('grantId',g.public_id,'grantRevision',g.revision,'grantEnvelope',encode(g.recipient_key_envelope,'hex'),
            'ownerId',owner.public_id,'recipientIdentity',a.heimdall_public_id,
            'ownerMaterial',encode(op.material,'hex'),'ownerRevision',op.revision,'ownerEpoch',op.key_epoch,'ownerGeneration',op.recovery_generation,
            'recipientMaterial',encode(rp.material,'hex'),'recipientRevision',rp.revision,'recipientEpoch',rp.key_epoch,'recipientGeneration',rp.recovery_generation)::text
            FROM target c JOIN cerberus.collection_grant g ON g.id=c.grant_id
            JOIN cerberus.account owner ON owner.id=c.account_id CROSS JOIN actor a
            LEFT JOIN cerberus.vault_protection op ON op.account_id=owner.id
            LEFT JOIN cerberus.vault_protection rp ON rp.account_id=a.id
            WHERE c.account_id<>a.id) AS grant
        """;
}
