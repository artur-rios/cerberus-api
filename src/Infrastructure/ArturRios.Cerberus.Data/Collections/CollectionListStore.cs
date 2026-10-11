using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Collections;

public sealed class CollectionListStore(IDbContextFactory<AppDbContext> factory) : ICollectionListStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false };

    public async Task<VaultResult<CollectionListPage>> ListAsync(CollectionListRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        if (request.PageSize <= 0 || request.After < 0 || request.After > ProtocolBinary.MaxInteger
            || request.Boundary is < 0 or > ProtocolBinary.MaxInteger) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            // One statement captures current authority, permitted collections, grant pins and page.
            // Exact selected-profile links filter before ordering/pagination. Collection content
            // cannot widen collection admission. Validate all visible foreign grants from this
            // snapshot, including evidence outside the returned page/highwater window.
            var snapshots = await db.Database.SqlQuery<Snapshot>($"""
                WITH actor AS (
                    SELECT a.* FROM cerberus.account a WHERE a.heimdall_public_id={request.Actor}
                ), session AS (
                    SELECT s.* FROM cerberus.vault_access_session s JOIN actor a ON a.id=s.account_id
                    WHERE a.state={(int)AccountState.Active}
                      AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='account' AND e.resource_id=a.public_id))
                      AND s.handle_verifier={request.AccessVerifier} AND NOT s.revoked
                      AND s.policy_revision>0 AND s.revocation_generation>0
                      AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation
                      AND s.issued_at<=statement_timestamp()
                      AND CASE WHEN a.renewal_enabled THEN s.expires_at>statement_timestamp() ELSE s.expires_at IS NULL END
                      AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile p
                          WHERE p.id=s.profile_id AND p.account_id=a.id AND p.deleted_at IS NULL
                            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='profile' AND e.resource_id=p.public_id))))
                ), collections AS (
                    SELECT c.*,g.id grant_id FROM cerberus.collection c
                    JOIN cerberus.account owner ON owner.id=c.account_id CROSS JOIN session s
                    LEFT JOIN cerberus.collection_grant g ON g.collection_id=c.id AND g.recipient_account_id=s.account_id
                      AND g.state={(int)CollectionGrantState.Active}
                      AND g.access IN ({(int)CollectionGrantAccess.ReadOnly},{(int)CollectionGrantAccess.ReadWrite})
                      AND g.revision>0 AND g.revision<={ProtocolBinary.MaxInteger}
                      AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='grant' AND e.resource_id=g.public_id))
                    WHERE owner.state={(int)AccountState.Active} AND c.deleted_at IS NULL
                      AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='account' AND e.resource_id=owner.public_id) OR (e.resource_kind='collection' AND e.resource_id=c.public_id))
                      AND (c.account_id=s.account_id OR g.id IS NOT NULL)
                      AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_collection pc WHERE pc.profile_id=s.profile_id AND pc.collection_id=c.id))
                ), visible AS (
                    SELECT * FROM collections
                ), boundary AS (
                    SELECT COALESCE({request.Boundary}::bigint,(SELECT MAX(r.server_sequence) FROM visible r),0) value
                ), page AS (
                    SELECT r.* FROM visible r CROSS JOIN boundary b WHERE r.server_sequence>{request.After} AND r.server_sequence<=b.value
                    ORDER BY r.server_sequence
                ), relevant_grants AS (
                    SELECT c.grant_id FROM visible c CROSS JOIN session s
                    WHERE c.account_id<>s.account_id AND c.grant_id IS NOT NULL
                )
                SELECT EXISTS(SELECT FROM actor a WHERE a.state={(int)AccountState.Active}
                    AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='account' AND e.resource_id=a.public_id))) AS actor_active,
                    EXISTS(SELECT FROM session) AS allowed,
                    EXISTS(SELECT FROM visible r GROUP BY r.server_sequence
                        HAVING r.server_sequence<=0 OR r.server_sequence>{ProtocolBinary.MaxInteger} OR COUNT(*)>1) AS corrupt_ordering,
                    (SELECT value FROM boundary) AS boundary,
                    EXISTS(SELECT FROM page OFFSET {request.PageSize}) AS has_more,
                    COALESCE((SELECT jsonb_agg(jsonb_build_object('collectionId',p.public_id,'revision',p.revision,
                        'serverSequence',p.server_sequence,'editedAt',p.edited_at,'envelope',encode(p.envelope,'hex'),'keyEpoch',p.key_epoch) ORDER BY p.server_sequence)
                        FROM (SELECT * FROM page LIMIT {request.PageSize}) p),'[]'::jsonb)::text AS items,
                    COALESCE((SELECT jsonb_agg(jsonb_build_object('collectionId',c.public_id,'collectionEpoch',c.key_epoch,
                        'collectionEnvelope',encode(c.envelope,'hex'),'grantId',g.public_id,'grantRevision',g.revision,
                        'grantEnvelope',encode(g.recipient_key_envelope,'hex'),'ownerId',owner.public_id,'recipientIdentity',a.heimdall_public_id,
                        'ownerMaterial',encode(op.material,'hex'),'ownerRevision',op.revision,'ownerEpoch',op.key_epoch,'ownerGeneration',op.recovery_generation,
                        'recipientMaterial',encode(rp.material,'hex'),'recipientRevision',rp.revision,'recipientEpoch',rp.key_epoch,'recipientGeneration',rp.recovery_generation))
                        FROM relevant_grants rg JOIN cerberus.collection_grant g ON g.id=rg.grant_id
                        JOIN cerberus.collection c ON c.id=g.collection_id JOIN cerberus.account owner ON owner.id=c.account_id CROSS JOIN actor a
                        LEFT JOIN cerberus.vault_protection op ON op.account_id=owner.id
                        LEFT JOIN cerberus.vault_protection rp ON rp.account_id=a.id),'[]'::jsonb)::text AS grants
                """).ToListAsync(ct);
            var snapshot = snapshots.Single();
            if (!snapshot.ActorActive) return new(Error: "not_found");
            if (!snapshot.Allowed) return new(Error: "vault_access_denied");
            if (snapshot.CorruptOrdering) return new(Error: "persistence_unavailable");
            var grants = JsonSerializer.Deserialize<GrantEvidence[]>(snapshot.Grants, Json)!;
            if (grants.Length > 0)
            {
                var first = grants[0];
                if (!CollectionGrantBinding.TryReadPins(Pins(first.RecipientMaterial, first.RecipientRevision,
                    first.RecipientEpoch, first.RecipientGeneration), out var recipient))
                    return new(Error: "persistence_unavailable");
                var owners = new Dictionary<Guid, ProtectionMaterial>();
                foreach (var evidence in grants)
                {
                    if (!owners.TryGetValue(evidence.OwnerId, out var owner))
                    {
                        if (!CollectionGrantBinding.TryReadPins(Pins(evidence.OwnerMaterial, evidence.OwnerRevision,
                            evidence.OwnerEpoch, evidence.OwnerGeneration), out owner))
                            return new(Error: "persistence_unavailable");
                        owners.Add(evidence.OwnerId, owner!);
                    }
                    if (!evidence.IsBound(owner!, recipient!)) return new(Error: "persistence_unavailable");
                }
            }
            var items = JsonSerializer.Deserialize<Item[]>(snapshot.Items, Json)!
                .Select(x => new CollectionListRow(x.CollectionId, x.Revision, x.ServerSequence, x.EditedAt, Convert.FromHexString(x.Envelope), x.KeyEpoch)).ToArray();
            return new(new(items, snapshot.Boundary, snapshot.HasMore));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        catch (Exception exception) when (exception is DbException or TimeoutException or JsonException or FormatException
            || exception is InvalidOperationException { InnerException: DbException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }

    private sealed record Item(Guid CollectionId, long Revision, long ServerSequence, DateTimeOffset EditedAt, string Envelope, long KeyEpoch);
    private sealed class Snapshot
    {
        public bool ActorActive { get; set; }
        public bool Allowed { get; set; }
        public bool CorruptOrdering { get; set; }
        public long Boundary { get; set; }
        public bool HasMore { get; set; }
        public string Items { get; set; } = "[]";
        public string Grants { get; set; } = "[]";
    }
    private static VaultProtection? Pins(string? material, long? revision, long? epoch, long? generation) =>
        material is null || revision is null || epoch is null || generation is null ? null : new()
        { Material = Convert.FromHexString(material), Revision = revision.Value, KeyEpoch = epoch.Value, RecoveryGeneration = generation.Value };

    private sealed record GrantEvidence(Guid CollectionId, long CollectionEpoch, string CollectionEnvelope,
        Guid GrantId, long GrantRevision, string GrantEnvelope, Guid OwnerId, Guid RecipientIdentity,
        string? OwnerMaterial, long? OwnerRevision, long? OwnerEpoch, long? OwnerGeneration,
        string? RecipientMaterial, long? RecipientRevision, long? RecipientEpoch, long? RecipientGeneration)
    {
        internal bool IsBound(ProtectionMaterial owner, ProtectionMaterial recipient) =>
            CollectionGrantBinding.IsBound(new() { PublicId = GrantId, Revision = GrantRevision, RecipientKeyEnvelope = Convert.FromHexString(GrantEnvelope) },
                new() { PublicId = CollectionId, KeyEpoch = CollectionEpoch, Envelope = Convert.FromHexString(CollectionEnvelope) }, OwnerId, RecipientIdentity, owner, recipient);
    }
}
