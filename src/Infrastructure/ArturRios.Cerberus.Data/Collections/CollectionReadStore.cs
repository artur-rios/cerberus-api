using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Collections;

public sealed class CollectionReadStore(IDbContextFactory<AppDbContext> factory) : ICollectionReadStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false };

    public async Task<VaultResult<CollectionReadDetails>> ReadAsync(CollectionReadRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        if (request.CollectionId == Guid.Empty) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            // One statement captures current authority and one public-ID target before
            // direct members and their upward ancestry. Independent aggregates avoid
            // relationship multiplication; descendants never become direct members.
            var snapshots = await db.Database.SqlQuery<Snapshot>($"""
                WITH RECURSIVE actor AS (
                    SELECT a.* FROM cerberus.account a WHERE a.heimdall_public_id={request.Actor}
                ), session AS (
                    SELECT s.* FROM cerberus.vault_access_session s JOIN actor a ON a.id=s.account_id
                    WHERE a.state={(int)AccountState.Active}
                      AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)
                      AND s.handle_verifier={request.AccessVerifier} AND NOT s.revoked
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
                    LEFT JOIN cerberus.collection_grant g ON g.collection_id=c.id AND g.recipient_account_id=s.account_id
                      AND g.state={(int)CollectionGrantState.Active}
                      AND g.access IN ({(int)CollectionGrantAccess.ReadOnly},{(int)CollectionGrantAccess.ReadWrite})
                      AND g.revision>0 AND g.revision<={ProtocolBinary.MaxInteger}
                      AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='grant' AND e.resource_id=g.public_id)
                    WHERE c.public_id={request.CollectionId} AND c.deleted_at IS NULL AND owner.state={(int)AccountState.Active}
                      AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e
                          WHERE (e.resource_kind='collection' AND e.resource_id=c.public_id) OR (e.resource_kind='account' AND e.resource_id=owner.public_id))
                      AND (c.account_id=s.account_id OR g.id IS NOT NULL)
                      AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_collection pc WHERE pc.profile_id=s.profile_id AND pc.collection_id=c.id))
                ), record_candidates AS MATERIALIZED (
                    SELECT r.id,r.public_id,r.account_id,r.folder_id FROM target c
                    JOIN cerberus.collection_record cr ON cr.collection_id=c.id AND cr.account_id=c.account_id
                    JOIN cerberus.record r ON r.id=cr.record_id AND r.account_id=c.account_id
                    WHERE r.deleted_at IS NULL
                      AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='record' AND e.resource_id=r.public_id)
                ), folder_candidates AS MATERIALIZED (
                    SELECT f.id,f.public_id,f.account_id FROM target c
                    JOIN cerberus.collection_folder cf ON cf.collection_id=c.id AND cf.account_id=c.account_id
                    JOIN cerberus.folder f ON f.id=cf.folder_id AND f.account_id=c.account_id
                    WHERE f.deleted_at IS NULL
                      AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
                ), ancestry AS (
                    SELECT f.id start_id,f.account_id,f.id,f.parent_folder_id,ARRAY[f.id] path,false cycle,
                        (f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)) hidden
                    FROM (
                        SELECT r.account_id,r.folder_id id FROM record_candidates r WHERE r.folder_id IS NOT NULL
                        UNION SELECT f.account_id,f.id FROM folder_candidates f
                    ) seeds JOIN cerberus.folder f ON f.id=seeds.id AND f.account_id=seeds.account_id
                    UNION ALL
                    SELECT t.start_id,t.account_id,f.id,f.parent_folder_id,t.path||f.id,f.id=ANY(t.path),
                        t.hidden OR f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
                    FROM ancestry t JOIN cerberus.folder f ON f.id=t.parent_folder_id AND f.account_id=t.account_id
                    WHERE NOT t.cycle
                ), complete_ancestry AS (
                    SELECT t.start_id FROM ancestry t GROUP BY t.start_id
                    HAVING NOT bool_or(t.hidden OR t.cycle) AND bool_or(t.parent_folder_id IS NULL)
                )
                SELECT EXISTS(SELECT FROM actor a WHERE a.state={(int)AccountState.Active}
                    AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)) AS actor_active,
                    EXISTS(SELECT FROM session) AS allowed,
                    EXISTS(SELECT FROM target c WHERE c.server_sequence<=0 OR c.server_sequence>{ProtocolBinary.MaxInteger}) AS corrupt_ordering,
                    EXISTS(SELECT FROM ancestry t WHERE t.cycle
                        AND NOT EXISTS(SELECT FROM ancestry h WHERE h.start_id=t.start_id AND h.hidden)) AS corrupt_ancestry,
                    EXISTS(SELECT FROM target c CROSS JOIN session s WHERE c.account_id<>s.account_id) AS foreign_target,
                    (SELECT jsonb_build_object('collectionId',c.public_id,'revision',c.revision,
                        'serverSequence',c.server_sequence,'editedAt',c.edited_at,'envelope',encode(c.envelope,'hex'),'keyEpoch',c.key_epoch,
                        'profileIds',COALESCE((SELECT jsonb_agg(p.public_id ORDER BY p.public_id)
                            FROM cerberus.profile_collection pc JOIN cerberus.profile p ON p.id=pc.profile_id CROSS JOIN session s
                            WHERE pc.collection_id=c.id AND p.account_id=s.account_id AND p.deleted_at IS NULL
                              AND (s.profile_id IS NULL OR p.id=s.profile_id)
                              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id)),'[]'::jsonb),
                        'recordIds',COALESCE((SELECT jsonb_agg(r.public_id ORDER BY r.public_id) FROM record_candidates r
                            WHERE r.folder_id IS NULL OR EXISTS(SELECT FROM complete_ancestry t WHERE t.start_id=r.folder_id)),'[]'::jsonb),
                        'folderIds',COALESCE((SELECT jsonb_agg(f.public_id ORDER BY f.public_id) FROM folder_candidates f
                            WHERE EXISTS(SELECT FROM complete_ancestry t WHERE t.start_id=f.id)),'[]'::jsonb))::text
                        FROM target c) AS item,
                    (SELECT jsonb_build_object('grantId',g.public_id,'grantRevision',g.revision,'grantEnvelope',encode(g.recipient_key_envelope,'hex'),
                        'ownerId',owner.public_id,'recipientIdentity',a.heimdall_public_id,
                        'ownerMaterial',encode(op.material,'hex'),'ownerRevision',op.revision,'ownerEpoch',op.key_epoch,'ownerGeneration',op.recovery_generation,
                        'recipientMaterial',encode(rp.material,'hex'),'recipientRevision',rp.revision,'recipientEpoch',rp.key_epoch,'recipientGeneration',rp.recovery_generation)::text
                        FROM target c JOIN cerberus.collection_grant g ON g.id=c.grant_id
                        JOIN cerberus.account owner ON owner.id=c.account_id CROSS JOIN actor a
                        LEFT JOIN cerberus.vault_protection op ON op.account_id=owner.id
                        LEFT JOIN cerberus.vault_protection rp ON rp.account_id=a.id
                        WHERE c.account_id<>a.id) AS grant
                """).ToListAsync(ct);
            var snapshot = snapshots.Single();
            if (!snapshot.ActorActive) return new(Error: "not_found");
            if (!snapshot.Allowed) return new(Error: "vault_access_denied");
            if (snapshot.CorruptOrdering || snapshot.CorruptAncestry) return new(Error: "persistence_unavailable");
            if (snapshot.Item is null) return new(Error: "not_found");
            var item = JsonSerializer.Deserialize<Item>(snapshot.Item, Json)!;
            var envelope = Convert.FromHexString(item.Envelope);
            if (snapshot.ForeignTarget)
            {
                // Exactly one native target: read each party's current pins once and
                // verify against the envelope captured by the same SQL statement.
                var evidence = snapshot.Grant is null ? null : JsonSerializer.Deserialize<GrantEvidence>(snapshot.Grant, Json);
                if (evidence is null
                    || !CollectionGrantBinding.TryReadPins(Pins(evidence.OwnerMaterial, evidence.OwnerRevision, evidence.OwnerEpoch, evidence.OwnerGeneration), out var owner)
                    || !CollectionGrantBinding.TryReadPins(Pins(evidence.RecipientMaterial, evidence.RecipientRevision, evidence.RecipientEpoch, evidence.RecipientGeneration), out var recipient)
                    || !CollectionGrantBinding.IsBound(
                        new() { PublicId = evidence.GrantId, Revision = evidence.GrantRevision, RecipientKeyEnvelope = Convert.FromHexString(evidence.GrantEnvelope) },
                        new() { PublicId = item.CollectionId, KeyEpoch = item.KeyEpoch, Envelope = envelope },
                        evidence.OwnerId, evidence.RecipientIdentity, owner!, recipient!))
                    return new(Error: "persistence_unavailable");
            }
            // Arrays and bytes are fresh snapshot-owned copies. The query layer's
            // shared CollectionProjection validates the final public projection.
            return new(new(new(item.CollectionId, item.Revision, item.ServerSequence, item.EditedAt, envelope, item.KeyEpoch),
                item.ProfileIds, item.RecordIds, item.FolderIds));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        catch (Exception exception) when (exception is DbException or TimeoutException or JsonException or FormatException
            || exception is InvalidOperationException { InnerException: DbException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }

    private static VaultProtection? Pins(string? material, long? revision, long? epoch, long? generation) =>
        material is null || revision is null || epoch is null || generation is null ? null : new()
        { Material = Convert.FromHexString(material), Revision = revision.Value, KeyEpoch = epoch.Value, RecoveryGeneration = generation.Value };

    private sealed record Item(Guid CollectionId, long Revision, long ServerSequence, DateTimeOffset EditedAt, string Envelope, long KeyEpoch,
        Guid[] ProfileIds, Guid[] RecordIds, Guid[] FolderIds);
    private sealed record GrantEvidence(Guid GrantId, long GrantRevision, string GrantEnvelope, Guid OwnerId, Guid RecipientIdentity,
        string? OwnerMaterial, long? OwnerRevision, long? OwnerEpoch, long? OwnerGeneration,
        string? RecipientMaterial, long? RecipientRevision, long? RecipientEpoch, long? RecipientGeneration);
    private sealed class Snapshot
    {
        public bool ActorActive { get; set; }
        public bool Allowed { get; set; }
        public bool CorruptOrdering { get; set; }
        public bool CorruptAncestry { get; set; }
        public bool ForeignTarget { get; set; }
        public string? Item { get; set; }
        public string? Grant { get; set; }
    }
}
