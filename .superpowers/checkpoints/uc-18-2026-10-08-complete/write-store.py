from pathlib import Path
r=Path('/tmp/cerberus-uc18-worktree');base=(r/'src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordListStore.cs').read_text()
(r/'src/Domain/ArturRios.Cerberus.Domain/Records/RecordReadContracts.cs').write_text('''using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Records;

public sealed record RecordReadRequest(Guid Actor, string AccessVerifier, Guid RecordId);
public sealed record RecordReadDetails(RecordListRow Record, Guid[] ProfileIds, Guid? FolderId, Guid[] CollectionIds);
public interface IRecordReadStore
{
    Task<VaultResult<RecordReadDetails>> ReadAsync(RecordReadRequest request, CancellationToken cancellationToken);
}
''')
sql=base[base.index('                WITH RECURSIVE actor'):base.index('                """).ToListAsync(ct);')]
start=sql.index('                ), roots AS (');end=sql.index('                ), ancestry AS (')
sql=sql[:start]+'''                ), candidates AS MATERIALIZED (
                    SELECT r.* FROM cerberus.record r JOIN cerberus.account owner ON owner.id=r.account_id CROSS JOIN session s
                    WHERE r.public_id={request.RecordId} AND owner.state={(int)AccountState.Active}
                      AND (r.account_id=s.account_id OR EXISTS(SELECT FROM collections c WHERE c.account_id=r.account_id))
                      AND r.deleted_at IS NULL
                      AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=r.public_id OR e.resource_id=owner.public_id)
'''+sql[end:]
start=sql.index('                ), boundary AS (');end=sql.index('                ), relevant_grants AS (')
sql=sql[:start]+sql[end:]
start=sql.index('                    EXISTS(SELECT FROM visible r GROUP BY');end=sql.index('                    COALESCE((SELECT jsonb_agg(jsonb_build_object(\'collectionId\'')
sql=sql[:start]+'''                    EXISTS(SELECT FROM visible r WHERE r.server_sequence<=0 OR r.server_sequence>{ProtocolBinary.MaxInteger}) AS corrupt_ordering,
                    (SELECT jsonb_build_object('recordId',r.public_id,'revision',r.revision,
                        'serverSequence',r.server_sequence,'editedAt',r.edited_at,'envelope',encode(r.envelope,'hex'),
                        'profileIds',COALESCE((SELECT jsonb_agg(p.public_id ORDER BY p.public_id)
                            FROM cerberus.profile_record pr JOIN cerberus.profile p ON p.id=pr.profile_id AND p.account_id=pr.account_id CROSS JOIN session s
                            WHERE pr.record_id=r.id AND pr.account_id=r.account_id AND p.account_id=s.account_id
                              AND (s.profile_id IS NULL OR s.profile_id=p.id) AND p.deleted_at IS NULL
                              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=p.public_id)),'[]'::jsonb),
                        'folderId',CASE WHEN r.folder_id IS NOT NULL AND EXISTS(SELECT FROM session s WHERE
                            (r.account_id=s.account_id AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN ancestry t ON t.id=pf.folder_id
                                WHERE pf.account_id=s.account_id AND pf.profile_id=s.profile_id AND t.record_id=r.id)))
                            OR EXISTS(SELECT FROM included i JOIN cerberus.collection_folder cf ON cf.collection_id=i.collection_id AND cf.account_id=r.account_id
                                JOIN ancestry t ON t.id=cf.folder_id WHERE i.record_id=r.id AND t.record_id=r.id))
                            THEN (SELECT f.public_id FROM cerberus.folder f WHERE f.id=r.folder_id AND f.account_id=r.account_id) ELSE NULL END,
                        'collectionIds',COALESCE((SELECT jsonb_agg(c.public_id ORDER BY c.public_id)
                            FROM included i JOIN cerberus.collection c ON c.id=i.collection_id WHERE i.record_id=r.id),'[]'::jsonb))::text
                        FROM visible r) AS item,
                    (SELECT jsonb_build_object('ownerId',owner.public_id,'recipientIdentity',a.heimdall_public_id,
                        'ownerMaterial',encode(op.material,'hex'),'ownerRevision',op.revision,'ownerEpoch',op.key_epoch,'ownerGeneration',op.recovery_generation,
                        'recipientMaterial',encode(rp.material,'hex'),'recipientRevision',rp.revision,'recipientEpoch',rp.key_epoch,'recipientGeneration',rp.recovery_generation)::text
                        FROM visible r JOIN cerberus.account owner ON owner.id=r.account_id CROSS JOIN actor a
                        LEFT JOIN cerberus.vault_protection op ON op.account_id=owner.id
                        LEFT JOIN cerberus.vault_protection rp ON rp.account_id=a.id
                        WHERE EXISTS(SELECT FROM relevant_grants)) AS protection,
'''+sql[end:]
start=sql.index("                        'grantEnvelope'");end=sql.index('                """') if '                """' in sql else len(sql)
sql=sql[:start]+'''                        'grantEnvelope',encode(g.recipient_key_envelope,'hex')))
                        FROM relevant_grants rg JOIN cerberus.collection_grant g ON g.id=rg.grant_id
                        JOIN cerberus.collection c ON c.id=g.collection_id),'[]'::jsonb)::text AS grants
'''
head='''using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Records;

public sealed class RecordReadStore(IDbContextFactory<AppDbContext> factory) : IRecordReadStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false };

    public async Task<VaultResult<RecordReadDetails>> ReadAsync(RecordReadRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Actor == Guid.Empty) return new(Error: "authentication_required");
        if (request.RecordId == Guid.Empty) return new(Error: "validation_failed");
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            // Target lookup is bounded before full upward ancestry. The same statement
            // captures current authority, visible references and all native evidence.
            // No later database reads, read locks, foreign locks or mutations.
            var snapshots = await db.Database.SqlQuery<Snapshot>($"""
'''
tail='''                """).ToListAsync(ct);
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
            return new(new(new(item.RecordId, item.Revision, item.ServerSequence, item.EditedAt, Convert.FromHexString(item.Envelope)),
                item.ProfileIds, item.FolderId, item.CollectionIds));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(Error: "persistence_unavailable"); }
        catch (Exception exception) when (exception is DbException or TimeoutException or JsonException or FormatException
            || exception is InvalidOperationException { InnerException: DbException or TimeoutException })
        { return new(Error: "persistence_unavailable"); }
    }

    private static VaultProtection? Pins(string? material, long? revision, long? epoch, long? generation) =>
        material is null || revision is null || epoch is null || generation is null ? null : new()
        { Material = Convert.FromHexString(material), Revision = revision.Value, KeyEpoch = epoch.Value, RecoveryGeneration = generation.Value };

    private sealed record Item(Guid RecordId, long Revision, long ServerSequence, DateTimeOffset EditedAt, string Envelope,
        Guid[] ProfileIds, Guid? FolderId, Guid[] CollectionIds);
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
'''
(r/'src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordReadStore.cs').write_text(head+sql+tail)
ledger=r/'.superpowers/sdd/2026-10-08-uc-18-get-record/progress.md';ledger.write_text(ledger.read_text()+'\nTask 1 RED: /tmp/cerberus-uc18-data-red.log exit1 expected CS0246 missing RecordReadStore/RecordReadDetails before product; behavioral cases written first.\n')
