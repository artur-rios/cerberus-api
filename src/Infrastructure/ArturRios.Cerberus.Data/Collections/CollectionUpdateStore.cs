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

public sealed class CollectionUpdateStore(IDbContextFactory<AppDbContext> factory) : ICollectionUpdateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        NumberHandling = JsonNumberHandling.Strict
    };

    public async Task<VaultResult<CollectionCreateDetails>> UpdateAsync(CollectionUpdateRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        if (string.IsNullOrWhiteSpace(request.AccessVerifier)) return new(Error: "vault_access_required");
        if (request.CollectionId == Guid.Empty || request.Input?.IsValid() != true) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Protection rotation and resource writers use the actor's account first.
            // A foreign denial never locks the owner graph or any grant/protection row.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR NO KEY UPDATE", ct);
            var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.HeimdallPublicId == request.Actor, ct);
            if (account is null || account.State != AccountState.Active
                || await db.TerminalErasures.AnyAsync(x => x.ResourceKind == "account" && x.ResourceId == account.PublicId, ct))
                return new(Error: "not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE", ct);
            var session = await db.VaultAccessSessions.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == account.Id && x.HandleVerifier == request.AccessVerifier, ct);
            if (session?.ProfileId is not null)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE id={session.ProfileId} AND account_id={account.Id} FOR UPDATE", ct);

            var initial = await SnapshotAsync(db, request, ct);
            var error = StateError(initial);
            if (error is not null) return new(Error: error);
            if (initial.ForeignTarget) return new(Error: ValidNative(initial) ? "vault_access_denied" : "persistence_unavailable");

            var target = JsonSerializer.Deserialize<Item>(initial.Item!, Json)!;
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE public_id={request.CollectionId} AND account_id={account.Id} FOR NO KEY UPDATE", ct);
            // Reload after all waits; statement time determines expiry and current scope.
            var current = await SnapshotAsync(db, request, ct);
            error = StateError(current);
            if (error is not null) return new(Error: error);
            if (current.ForeignTarget) return new(Error: ValidNative(current) ? "vault_access_denied" : "persistence_unavailable");
            var lockedTarget = target.Id;
            target = JsonSerializer.Deserialize<Item>(current.Item!, Json)!;
            if (target.Id != lockedTarget || target.AccountId != account.Id || current.Selection != session?.ProfileId)
                return new(Error: "revision_conflict");

            if (!Safe(target.Revision) || !Safe(target.Sequence) || !Safe(target.KeyEpoch)
                || !DateTimeOffset.TryParse(target.EditedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var storedTime)
                || storedTime.Offset != TimeSpan.Zero || storedTime.Ticks < 10 || storedTime.Ticks % 10 != 0)
                return new(Error: "persistence_unavailable");
            var original = Convert.FromHexString(target.Envelope);
            var old = JsonSerializer.Deserialize<EncryptedEnvelope>(original, Json);
            if (old?.IsValid() != true || old.KeyEpoch != target.KeyEpoch) return new(Error: "persistence_unavailable");
            var input = request.Input;
            if (target.Revision != input.ExpectedRevision || target.Revision == ProtocolBinary.MaxInteger)
                return new(Error: "revision_conflict");
            if (input.Envelope.KeyEpoch != target.KeyEpoch
                || input.Envelope != old && (input.Envelope.KeySalt == old.KeySalt || input.Envelope.Nonce == old.Nonce))
                return new(Error: "validation_failed");

            var replacement = JsonSerializer.SerializeToUtf8Bytes(input.Envelope, Json);
            var edited = input.EditedAt.AddTicks(-(input.EditedAt.Ticks % TimeSpan.TicksPerMicrosecond));
            var stamp = Guid.NewGuid();
            var parameters = Parameters(request).Concat(new object[]
            {
                new NpgsqlParameter("replacement", replacement), new NpgsqlParameter("edited", edited),
                new NpgsqlParameter("stamp", stamp), new NpgsqlParameter("expected", input.ExpectedRevision),
                new NpgsqlParameter("previous_sequence", target.Sequence), new NpgsqlParameter("original", original),
                new NpgsqlParameter("epoch", target.KeyEpoch), new NpgsqlParameter("owner", account.Id),
                new NpgsqlParameter("id", target.Id), new NpgsqlParameter("original_stamp", target.Stamp),
                new NpgsqlParameter("selection", NpgsqlDbType.Bigint) { Value = (object?)session?.ProfileId ?? DBNull.Value }
            }).ToArray();
            // Native grants bind collection identity and epoch, not these opaque bytes.
            // The owner path neither validates nor rewrites grants or recipient wrappers.
            var changed = await db.Database.ExecuteSqlRawAsync(Authority + """
                UPDATE cerberus.collection AS c SET envelope=@replacement,edited_at=@edited,revision=c.revision+1,
                  server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp
                FROM target t CROSS JOIN session s
                WHERE c.id=t.id AND c.id=@id AND c.public_id=@target AND c.account_id=@owner AND c.account_id=s.account_id
                  AND c.deleted_at IS NULL AND c.revision=@expected AND c.server_sequence=@previous_sequence
                  AND c.envelope=@original AND c.key_epoch=@epoch AND c.concurrency_stamp=@original_stamp
                  AND t.revision=@expected AND t.server_sequence=@previous_sequence AND t.envelope=@original AND t.key_epoch=@epoch
                  AND s.profile_id IS NOT DISTINCT FROM @selection
                """, parameters, ct);
            if (changed != 1)
            {
                var after = await SnapshotAsync(db, request, ct);
                return new(Error: StateError(after) ?? (after.ForeignTarget
                    ? ValidNative(after) ? "vault_access_denied" : "persistence_unavailable"
                    : "revision_conflict"));
            }

            var result = await db.Collections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == target.Id, ct);
            if (result is null || result.PublicId != request.CollectionId || result.AccountId != account.Id
                || result.Revision != target.Revision + 1 || !Safe(result.ServerSequence) || result.ServerSequence <= target.Sequence
                || result.EditedAt != edited || result.KeyEpoch != target.KeyEpoch || result.ConcurrencyStamp != stamp
                || !result.Envelope.AsSpan().SequenceEqual(replacement) || result.DeletedAt is not null || result.PurgeAt != target.PurgeAt)
                return new(Error: "persistence_unavailable");
            await tx.CommitAsync(ct);
            return new(new(result.PublicId, result.Revision, result.ServerSequence, result.EditedAt));
        }
        catch (PostgresException exception) when (exception.SqlState == "2200H") { return new(Error: "revision_conflict"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        catch (Exception exception) when (exception is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            || exception is InvalidOperationException { InnerException: DbException or DbUpdateException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }

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

    private static object[] Parameters(CollectionUpdateRequest request) =>
    [
        new NpgsqlParameter("actor", request.Actor), new NpgsqlParameter("verifier", request.AccessVerifier),
        new NpgsqlParameter("target", request.CollectionId), new NpgsqlParameter("active", (object)(int)AccountState.Active),
        new NpgsqlParameter("grant_active", (object)(int)CollectionGrantState.Active),
        new NpgsqlParameter("read_only", (object)(int)CollectionGrantAccess.ReadOnly),
        new NpgsqlParameter("read_write", (object)(int)CollectionGrantAccess.ReadWrite), new NpgsqlParameter("max", ProtocolBinary.MaxInteger)
    ];

    private static async Task<Snapshot> SnapshotAsync(AppDbContext db, CollectionUpdateRequest request, CancellationToken ct) =>
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
