using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Folders;

public sealed class FolderReadStore(IDbContextFactory<AppDbContext> factory) : IFolderReadStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false };

    public async Task<VaultResult<FolderReadDetails>> ReadAsync(FolderReadRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        if (request.FolderId == Guid.Empty) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            // Target lookup is bounded before full upward ancestry. The same statement
            // captures current authority, visible references and all native evidence.
            // No later database reads, read locks, foreign locks or mutations.
            var snapshots = await db.Database.SqlQuery<Snapshot>($"""
                WITH RECURSIVE actor AS (
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
                ), candidates AS MATERIALIZED (
                    SELECT r.* FROM cerberus.folder r JOIN cerberus.account owner ON owner.id=r.account_id CROSS JOIN session s
                    WHERE r.public_id={request.FolderId} AND owner.state={(int)AccountState.Active}
                      AND (r.account_id=s.account_id OR EXISTS(SELECT FROM collections c WHERE c.account_id=r.account_id))
                      AND r.deleted_at IS NULL
                      AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='folder' AND e.resource_id=r.public_id) OR (e.resource_kind='account' AND e.resource_id=owner.public_id))
                ), ancestry AS (
                    SELECT r.id folder_id,r.account_id,r.id,r.parent_folder_id,ARRAY[r.id] path,false cycle,
                        (r.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='folder' AND e.resource_id=r.public_id))) hidden
                    FROM candidates r
                    UNION ALL
                    SELECT t.folder_id,t.account_id,f.id,f.parent_folder_id,t.path||f.id,f.id=ANY(t.path),
                        t.hidden OR f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='folder' AND e.resource_id=f.public_id))
                    FROM ancestry t JOIN cerberus.folder f ON f.id=t.parent_folder_id AND f.account_id=t.account_id
                    WHERE NOT t.cycle
                ), included AS (
                    SELECT r.id folder_id,c.id collection_id,c.grant_id FROM candidates r JOIN collections c ON c.account_id=r.account_id
                    WHERE EXISTS(SELECT FROM cerberus.collection_folder cf JOIN ancestry t ON t.id=cf.folder_id
                           WHERE cf.collection_id=c.id AND cf.account_id=r.account_id AND t.folder_id=r.id)
                ), scoped AS (
                    SELECT r.* FROM candidates r CROSS JOIN session s WHERE
                      (r.account_id=s.account_id AND (s.profile_id IS NULL
                        OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN ancestry t ON t.id=pf.folder_id
                            WHERE pf.profile_id=s.profile_id AND pf.account_id=s.account_id AND t.folder_id=r.id)))
                      OR EXISTS(SELECT FROM included i WHERE i.folder_id=r.id)
                ), visible AS (
                    SELECT r.* FROM scoped r WHERE
                      NOT EXISTS(SELECT FROM ancestry t WHERE t.folder_id=r.id AND (t.hidden OR t.cycle))
                      AND EXISTS(SELECT FROM ancestry t WHERE t.folder_id=r.id AND t.parent_folder_id IS NULL)
                ), relevant_grants AS (
                    SELECT DISTINCT i.grant_id FROM included i JOIN visible r ON r.id=i.folder_id CROSS JOIN session s
                    WHERE r.account_id<>s.account_id AND i.grant_id IS NOT NULL
                )
                SELECT EXISTS(SELECT FROM actor a WHERE a.state={(int)AccountState.Active}
                    AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='account' AND e.resource_id=a.public_id))) AS actor_active,
                    EXISTS(SELECT FROM session) AS allowed,
                    EXISTS(SELECT FROM scoped r JOIN ancestry t ON t.folder_id=r.id WHERE t.cycle
                        AND NOT EXISTS(SELECT FROM ancestry hidden WHERE hidden.folder_id=r.id AND hidden.hidden)) AS corrupt_ancestry,
                    EXISTS(SELECT FROM visible r WHERE r.server_sequence<=0 OR r.server_sequence>{ProtocolBinary.MaxInteger}) AS corrupt_ordering,
                    (SELECT jsonb_build_object('folderId',r.public_id,'revision',r.revision,
                        'serverSequence',r.server_sequence,'editedAt',r.edited_at,'envelope',encode(r.envelope,'hex'),
                        'profileIds',COALESCE((SELECT jsonb_agg(p.public_id ORDER BY p.public_id)
                            FROM cerberus.profile_folder pr JOIN cerberus.profile p ON p.id=pr.profile_id AND p.account_id=pr.account_id CROSS JOIN session s
                            WHERE pr.folder_id=r.id AND pr.account_id=r.account_id AND p.account_id=s.account_id
                              AND (s.profile_id IS NULL OR s.profile_id=p.id) AND p.deleted_at IS NULL
                              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='profile' AND e.resource_id=p.public_id))),'[]'::jsonb),
                        'parentFolderId',CASE WHEN r.parent_folder_id IS NOT NULL AND EXISTS(SELECT FROM session s WHERE
                            (r.account_id=s.account_id AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN ancestry t ON t.id=pf.folder_id
                                WHERE pf.account_id=s.account_id AND pf.profile_id=s.profile_id AND t.folder_id=r.id AND t.id<>r.id)))
                            OR EXISTS(SELECT FROM included i JOIN cerberus.collection_folder cf ON cf.collection_id=i.collection_id AND cf.account_id=r.account_id
                                JOIN ancestry t ON t.id=cf.folder_id WHERE i.folder_id=r.id AND t.folder_id=r.id AND t.id<>r.id))
                            THEN (SELECT f.public_id FROM cerberus.folder f WHERE f.id=r.parent_folder_id AND f.account_id=r.account_id) ELSE NULL END,
                        'collectionIds',COALESCE((SELECT jsonb_agg(c.public_id ORDER BY c.public_id)
                            FROM included i JOIN cerberus.collection c ON c.id=i.collection_id WHERE i.folder_id=r.id),'[]'::jsonb))::text
                        FROM visible r) AS item,
                    (SELECT jsonb_build_object('ownerId',owner.public_id,'recipientIdentity',a.heimdall_public_id,
                        'ownerMaterial',encode(op.material,'hex'),'ownerRevision',op.revision,'ownerEpoch',op.key_epoch,'ownerGeneration',op.recovery_generation,
                        'recipientMaterial',encode(rp.material,'hex'),'recipientRevision',rp.revision,'recipientEpoch',rp.key_epoch,'recipientGeneration',rp.recovery_generation)::text
                        FROM visible r JOIN cerberus.account owner ON owner.id=r.account_id CROSS JOIN actor a
                        LEFT JOIN cerberus.vault_protection op ON op.account_id=owner.id
                        LEFT JOIN cerberus.vault_protection rp ON rp.account_id=a.id
                        WHERE EXISTS(SELECT FROM relevant_grants)) AS protection,
                    COALESCE((SELECT jsonb_agg(jsonb_build_object('collectionId',c.public_id,'collectionEpoch',c.key_epoch,
                        'collectionEnvelope',encode(c.envelope,'hex'),'grantId',g.public_id,'grantRevision',g.revision,
                        'grantEnvelope',encode(g.recipient_key_envelope,'hex')))
                        FROM relevant_grants rg JOIN cerberus.collection_grant g ON g.id=rg.grant_id
                        JOIN cerberus.collection c ON c.id=g.collection_id),'[]'::jsonb)::text AS grants
                """).ToListAsync(ct);
            var snapshot = snapshots.Single();
            if (!snapshot.ActorActive) return new(Error: "not_found");
            if (!snapshot.Allowed) return new(Error: "vault_access_denied");
            if (snapshot.CorruptOrdering || snapshot.CorruptAncestry) return new(Error: "persistence_unavailable");
            if (snapshot.Item is null) return new(Error: "not_found");
            var grants = JsonSerializer.Deserialize<GrantEvidence[]>(snapshot.Grants, Json)!;
            if (grants.Length > 0)
            {
                // One owner per target: capture and validate each party's pins once,
                // then bind every current collection grant contributing to this get.
                var pins = snapshot.Protection is null ? null : JsonSerializer.Deserialize<ProtectionEvidence>(snapshot.Protection, Json);
                if (pins is null || !CollectionGrantBinding.TryReadPins(Pins(pins.OwnerMaterial, pins.OwnerRevision, pins.OwnerEpoch, pins.OwnerGeneration), out var owner)
                    || !CollectionGrantBinding.TryReadPins(Pins(pins.RecipientMaterial, pins.RecipientRevision, pins.RecipientEpoch, pins.RecipientGeneration), out var recipient))
                    return new(Error: "persistence_unavailable");
                foreach (var evidence in grants)
                    if (!CollectionGrantBinding.IsBound(
                        new() { PublicId = evidence.GrantId, Revision = evidence.GrantRevision, RecipientKeyEnvelope = Convert.FromHexString(evidence.GrantEnvelope) },
                        new() { PublicId = evidence.CollectionId, KeyEpoch = evidence.CollectionEpoch, Envelope = Convert.FromHexString(evidence.CollectionEnvelope) },
                        pins.OwnerId, pins.RecipientIdentity, owner!, recipient!))
                        return new(Error: "persistence_unavailable");
            }
            var item = JsonSerializer.Deserialize<Item>(snapshot.Item, Json)!;
            return new(new(new(item.FolderId, item.Revision, item.ServerSequence, item.EditedAt, Convert.FromHexString(item.Envelope)),
                item.ProfileIds, item.ParentFolderId, item.CollectionIds));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        catch (Exception exception) when (exception is DbException or TimeoutException or JsonException or FormatException
            || exception is InvalidOperationException { InnerException: DbException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }

    private static VaultProtection? Pins(string? material, long? revision, long? epoch, long? generation) =>
        material is null || revision is null || epoch is null || generation is null ? null : new()
        { Material = Convert.FromHexString(material), Revision = revision.Value, KeyEpoch = epoch.Value, RecoveryGeneration = generation.Value };

    private sealed record Item(Guid FolderId, long Revision, long ServerSequence, DateTimeOffset EditedAt, string Envelope,
        Guid[] ProfileIds, Guid? ParentFolderId, Guid[] CollectionIds);
    private sealed class Snapshot
    {
        public bool ActorActive { get; set; }
        public bool Allowed { get; set; }
        public bool CorruptAncestry { get; set; }
        public bool CorruptOrdering { get; set; }
        public string? Item { get; set; }
        public string? Protection { get; set; }
        public string Grants { get; set; } = "[]";
    }
    private sealed record GrantEvidence(Guid CollectionId, long CollectionEpoch, string CollectionEnvelope,
        Guid GrantId, long GrantRevision, string GrantEnvelope);
    private sealed record ProtectionEvidence(Guid OwnerId, Guid RecipientIdentity,
        string? OwnerMaterial, long? OwnerRevision, long? OwnerEpoch, long? OwnerGeneration,
        string? RecipientMaterial, long? RecipientRevision, long? RecipientEpoch, long? RecipientGeneration);
}
