using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Records;

public sealed class RecordListStore(IDbContextFactory<AppDbContext> factory) : IRecordListStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false };

    public async Task<VaultResult<RecordListPage>> ListAsync(RecordListRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        if (request.PageSize <= 0 || request.After < 0 || request.After > ProtocolBinary.MaxInteger
            || request.Boundary is < 0 or > ProtocolBinary.MaxInteger) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            // One statement captures authorization, complete ancestry, permitted inventory,
            // grant signatures/pins, and the page. Never re-read authority after this snapshot.
            var snapshots = await db.Database.SqlQuery<Snapshot>($"""
                WITH RECURSIVE actor AS (
                    SELECT a.* FROM cerberus.account a WHERE a.heimdall_public_id={request.Actor}
                ), session AS (
                    SELECT s.* FROM cerberus.vault_access_session s JOIN actor a ON a.id=s.account_id
                    WHERE a.state={(int)AccountState.Active}
                      AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=a.public_id)
                      AND s.handle_verifier={request.AccessVerifier} AND NOT s.revoked
                      AND s.policy_revision>0 AND s.revocation_generation>0
                      AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation
                      AND s.issued_at<=statement_timestamp()
                      AND CASE WHEN a.renewal_enabled THEN s.expires_at>statement_timestamp() ELSE s.expires_at IS NULL END
                      AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile p
                          WHERE p.id=s.profile_id AND p.account_id=a.id AND p.deleted_at IS NULL
                            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=p.public_id)))
                ), candidates AS (
                    SELECT r.* FROM cerberus.record r JOIN cerberus.account owner ON owner.id=r.account_id
                    WHERE EXISTS(SELECT FROM session) AND owner.state={(int)AccountState.Active}
                      AND r.deleted_at IS NULL
                      AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=r.public_id OR e.resource_id=owner.public_id)
                ), ancestry AS (
                    SELECT r.id record_id,r.account_id,f.id,f.parent_folder_id,ARRAY[f.id] path,false cycle,
                        (f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=f.public_id)) hidden
                    FROM candidates r JOIN cerberus.folder f ON f.id=r.folder_id AND f.account_id=r.account_id
                    UNION ALL
                    SELECT t.record_id,t.account_id,f.id,f.parent_folder_id,t.path||f.id,f.id=ANY(t.path),
                        t.hidden OR f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=f.public_id)
                    FROM ancestry t JOIN cerberus.folder f ON f.id=t.parent_folder_id AND f.account_id=t.account_id
                    WHERE NOT t.cycle
                ), collections AS (
                    SELECT c.*,g.id grant_id FROM cerberus.collection c
                    JOIN cerberus.account owner ON owner.id=c.account_id CROSS JOIN session s
                    LEFT JOIN cerberus.collection_grant g ON g.collection_id=c.id AND g.recipient_account_id=s.account_id
                      AND g.state={(int)CollectionGrantState.Active}
                      AND g.access IN ({(int)CollectionGrantAccess.ReadOnly},{(int)CollectionGrantAccess.ReadWrite})
                      AND g.revision>0 AND g.revision<={ProtocolBinary.MaxInteger}
                      AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=g.public_id)
                    WHERE owner.state={(int)AccountState.Active} AND c.deleted_at IS NULL
                      AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=owner.public_id OR e.resource_id=c.public_id)
                      AND (c.account_id=s.account_id OR g.id IS NOT NULL)
                      AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_collection pc WHERE pc.profile_id=s.profile_id AND pc.collection_id=c.id))
                ), included AS (
                    SELECT r.id record_id,c.id collection_id,c.grant_id FROM candidates r JOIN collections c ON c.account_id=r.account_id
                    WHERE EXISTS(SELECT FROM cerberus.collection_record cr WHERE cr.collection_id=c.id AND cr.record_id=r.id AND cr.account_id=r.account_id)
                       OR EXISTS(SELECT FROM cerberus.collection_folder cf JOIN ancestry t ON t.id=cf.folder_id
                           WHERE cf.collection_id=c.id AND cf.account_id=r.account_id AND t.record_id=r.id)
                ), scoped AS (
                    SELECT r.* FROM candidates r CROSS JOIN session s WHERE
                      (r.account_id=s.account_id AND (s.profile_id IS NULL
                        OR EXISTS(SELECT FROM cerberus.profile_record pr WHERE pr.profile_id=s.profile_id AND pr.account_id=s.account_id AND pr.record_id=r.id)
                        OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN ancestry t ON t.id=pf.folder_id
                            WHERE pf.profile_id=s.profile_id AND pf.account_id=s.account_id AND t.record_id=r.id)))
                      OR EXISTS(SELECT FROM included i WHERE i.record_id=r.id)
                ), visible AS (
                    SELECT r.* FROM scoped r WHERE
                      NOT EXISTS(SELECT FROM ancestry t WHERE t.record_id=r.id AND (t.hidden OR t.cycle))
                      AND (r.folder_id IS NULL OR EXISTS(SELECT FROM ancestry t WHERE t.record_id=r.id AND t.parent_folder_id IS NULL))
                ), boundary AS (
                    SELECT COALESCE({request.Boundary}::bigint,(SELECT MAX(r.server_sequence) FROM visible r),0) value
                ), page AS (
                    SELECT r.* FROM visible r CROSS JOIN boundary b WHERE r.server_sequence>{request.After} AND r.server_sequence<=b.value
                    ORDER BY r.server_sequence
                ), relevant_grants AS (
                    SELECT DISTINCT i.grant_id FROM included i JOIN visible r ON r.id=i.record_id CROSS JOIN session s
                    WHERE r.account_id<>s.account_id AND i.grant_id IS NOT NULL
                )
                SELECT EXISTS(SELECT FROM actor a WHERE a.state={(int)AccountState.Active}
                    AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=a.public_id)) AS actor_active,
                    EXISTS(SELECT FROM session) AS allowed,
                    EXISTS(SELECT FROM scoped r JOIN ancestry t ON t.record_id=r.id WHERE t.cycle) AS corrupt_ancestry,
                    EXISTS(SELECT FROM visible r WHERE r.server_sequence<=0 OR r.server_sequence>{ProtocolBinary.MaxInteger}
                        OR EXISTS(SELECT FROM visible other WHERE other.id<>r.id AND other.server_sequence=r.server_sequence)) AS corrupt_ordering,
                    (SELECT value FROM boundary) AS boundary,
                    EXISTS(SELECT FROM page OFFSET {request.PageSize}) AS has_more,
                    COALESCE((SELECT jsonb_agg(jsonb_build_object('recordId',p.public_id,'revision',p.revision,
                        'serverSequence',p.server_sequence,'editedAt',p.edited_at,'envelope',encode(p.envelope,'hex')) ORDER BY p.server_sequence)
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
            if (snapshot.CorruptOrdering || snapshot.CorruptAncestry) return new(Error: "persistence_unavailable");
            foreach (var evidence in JsonSerializer.Deserialize<GrantEvidence[]>(snapshot.Grants, Json)!)
                if (!evidence.IsBound()) return new(Error: "persistence_unavailable");
            var items = JsonSerializer.Deserialize<Item[]>(snapshot.Items, Json)!
                .Select(x => new RecordListRow(x.RecordId, x.Revision, x.ServerSequence, x.EditedAt, Convert.FromHexString(x.Envelope))).ToArray();
            return new(new(items, snapshot.Boundary, snapshot.HasMore));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        catch (Exception exception) when (exception is DbException or TimeoutException or JsonException or FormatException
            || exception is InvalidOperationException { InnerException: DbException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }

    private sealed record Item(Guid RecordId, long Revision, long ServerSequence, DateTimeOffset EditedAt, string Envelope);
    private sealed class Snapshot
    {
        public bool ActorActive { get; set; }
        public bool Allowed { get; set; }
        public bool CorruptAncestry { get; set; }
        public bool CorruptOrdering { get; set; }
        public long Boundary { get; set; }
        public bool HasMore { get; set; }
        public string Items { get; set; } = "[]";
        public string Grants { get; set; } = "[]";
    }
    private sealed record GrantEvidence(Guid CollectionId, long CollectionEpoch, string CollectionEnvelope,
        Guid GrantId, long GrantRevision, string GrantEnvelope, Guid OwnerId, Guid RecipientIdentity,
        string? OwnerMaterial, long? OwnerRevision, long? OwnerEpoch, long? OwnerGeneration,
        string? RecipientMaterial, long? RecipientRevision, long? RecipientEpoch, long? RecipientGeneration)
    {
        internal bool IsBound()
        {
            static VaultProtection? Pins(string? material, long? revision, long? epoch, long? generation) =>
                material is null || revision is null || epoch is null || generation is null ? null : new()
                { Material = Convert.FromHexString(material), Revision = revision.Value, KeyEpoch = epoch.Value, RecoveryGeneration = generation.Value };
            return CollectionGrantBinding.TryReadPins(Pins(OwnerMaterial, OwnerRevision, OwnerEpoch, OwnerGeneration), out var owner)
                && CollectionGrantBinding.TryReadPins(Pins(RecipientMaterial, RecipientRevision, RecipientEpoch, RecipientGeneration), out var recipient)
                && CollectionGrantBinding.IsBound(new() { PublicId = GrantId, Revision = GrantRevision, RecipientKeyEnvelope = Convert.FromHexString(GrantEnvelope) },
                    new() { PublicId = CollectionId, KeyEpoch = CollectionEpoch, Envelope = Convert.FromHexString(CollectionEnvelope) }, OwnerId, RecipientIdentity, owner!, recipient!);
        }
    }
}
