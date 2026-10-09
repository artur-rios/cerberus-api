using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
namespace ArturRios.Cerberus.Data.Records;

public sealed class RecordMoveStore(IDbContextFactory<AppDbContext> factory):IRecordMoveStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {PropertyNameCaseInsensitive=false,AllowDuplicateProperties=false,NumberHandling=JsonNumberHandling.Strict};

    public async Task<VaultResult<RecordMoveDetails>> MoveAsync(RecordMoveRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        if(request.RecordId==Guid.Empty || request.FolderId==Guid.Empty || !Safe(request.ExpectedRevision))return new(Error:"validation_failed");
        try
        {
            await using var db=await factory.CreateDbContextAsync(ct);
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR NO KEY UPDATE",ct);
            var account=await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.HeimdallPublicId==request.Actor,ct);
            if(account is null || account.State!=AccountState.Active || await db.TerminalErasures.AnyAsync(x=>x.ResourceId==account.PublicId,ct))return new(Error:"not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE",ct);
            var initial=await SnapshotAsync(db,request,ct);var error=StateError(initial,account.Id);
            if(error is not null)return new(Error:error);
            var item=JsonSerializer.Deserialize<Item>(initial.Item!,Json)!;
            var original=await db.Records.AsNoTracking().SingleAsync(x=>x.Id==item.Id,ct);
            var originalParent=original.FolderId;
            var selection=await db.VaultAccessSessions.Where(x=>x.AccountId==account.Id && x.HandleVerifier==request.AccessVerifier).Select(x=>x.ProfileId).SingleAsync(ct);
            if(selection is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE account_id={account.Id} AND id={selection} FOR NO KEY UPDATE",ct);
                error=StateError(await SnapshotAsync(db,request,ct),account.Id);if(error is not null)return new(Error:error);
            }
            var collectionIds=initial.SourceCollections.Concat(initial.ResultCollections).Distinct().ToArray();
            var collections=await db.Collections.FromSqlInterpolated($"""
                SELECT c.* FROM cerberus.collection c WHERE c.account_id={account.Id} AND c.id=ANY({collectionIds})
                ORDER BY c.public_id FOR NO KEY UPDATE
                """).AsNoTracking().ToListAsync(ct);
            error=StateError(await SnapshotAsync(db,request,ct),account.Id);if(error is not null)return new(Error:error);
            var grants=await db.CollectionGrants.FromSqlInterpolated($"""
                SELECT g.* FROM cerberus.collection_grant g JOIN cerberus.collection c ON c.id=g.collection_id
                WHERE c.account_id={account.Id} AND g.id=ANY({initial.ResultGrantIds}) ORDER BY g.public_id FOR SHARE OF g
                """).AsNoTracking().ToListAsync(ct);
            error=StateError(await SnapshotAsync(db,request,ct),account.Id);if(error is not null)return new(Error:error);
            var folderIds=new[]{originalParent,initial.DestinationId}.OfType<long>().Distinct().ToArray();
            var folders=await db.Folders.FromSqlInterpolated($"""
                SELECT f.* FROM cerberus.folder f WHERE f.account_id={account.Id} AND f.id=ANY({folderIds})
                ORDER BY f.public_id FOR NO KEY UPDATE
                """).AsNoTracking().ToListAsync(ct);
            error=StateError(await SnapshotAsync(db,request,ct),account.Id);if(error is not null)return new(Error:error);
            // Only a structural parent is replaced. Non-key content locks allow harmless
            // FK references; newly contributing earlier authority still needs reload.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.record WHERE account_id={account.Id} AND id={item.Id} FOR NO KEY UPDATE",ct);
            var current=await SnapshotAsync(db,request,ct);error=StateError(current,account.Id);if(error is not null)return new(Error:error);
            if(!ValidResultNative(current))return new(Error:"persistence_unavailable");
            var heldCollections=collections.Select(x=>x.Id).ToArray();var heldGrants=grants.Select(x=>x.Id).ToArray();
            original=await db.Records.AsNoTracking().SingleAsync(x=>x.Id==item.Id,ct);
            if(original.FolderId!=originalParent || current.SourceCollections.Concat(current.ResultCollections).Except(heldCollections).Any()
                || current.ResultGrantIds.Except(heldGrants).Any() || current.DestinationId!=initial.DestinationId)return new(Error:"revision_conflict");
            var envelope=JsonSerializer.Deserialize<EncryptedEnvelope>(original.Envelope,Json);
            if(envelope?.IsValid()!=true || !Safe(original.Revision) || !Safe(original.ServerSequence)
                || original.EditedAt.Offset!=TimeSpan.Zero || original.EditedAt.Ticks<10 || original.EditedAt.Ticks%10!=0 || original.PurgeAt is not null)
                return new(Error:"persistence_unavailable");
            if(original.Revision!=request.ExpectedRevision || original.Revision==ProtocolBinary.MaxInteger)return new(Error:"revision_conflict");
            var parentChanges=originalParent!=current.DestinationId;
            var changedParents=parentChanges?folders.Where(x=>x.Id==originalParent || x.Id==current.DestinationId).ToArray():[];
            if(changedParents.Any(x=>!Safe(x.Revision) || !Safe(x.ServerSequence)))return new(Error:"persistence_unavailable");
            if(changedParents.Any(x=>x.Revision==ProtocolBinary.MaxInteger))return new(Error:"revision_conflict");
            var parameters=Parameters(request).Concat(new object[]{new NpgsqlParameter("expected",request.ExpectedRevision),
                new NpgsqlParameter("previous_sequence",original.ServerSequence),new NpgsqlParameter("original",original.Envelope),
                new NpgsqlParameter("parent",NpgsqlDbType.Bigint){Value=(object?)originalParent??DBNull.Value},
                new NpgsqlParameter("held_collections",heldCollections),new NpgsqlParameter("held_grants",heldGrants),
                new NpgsqlParameter("native_evidence",NpgsqlDbType.Jsonb){Value=current.NativeEvidence},new NpgsqlParameter("stamp",Guid.NewGuid())}).ToArray();
            var changed=await db.Database.ExecuteSqlRawAsync(Authority+"""
                UPDATE cerberus.record AS r SET folder_id=(SELECT id FROM destination_visible),revision=r.revision+1,
                  server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp
                FROM visible v CROSS JOIN session s CROSS JOIN native_result n CROSS JOIN move_scope m
                WHERE r.id=v.id AND r.account_id=s.account_id AND r.public_id=@target
                  AND m.allowed AND NOT m.corrupt AND m.subset AND n.evidence=@native_evidence
                  AND r.revision=@expected AND v.revision=@expected AND v.server_sequence=@previous_sequence
                  AND v.envelope=@original AND v.folder_id IS NOT DISTINCT FROM @parent AND r.purge_at IS NULL
                  AND NOT EXISTS(SELECT FROM source_collections c WHERE NOT(c.id=ANY(@held_collections)))
                  AND NOT EXISTS(SELECT FROM result_collections c WHERE NOT(c.id=ANY(@held_collections)))
                  AND NOT EXISTS(SELECT FROM result_grants g WHERE NOT(g.id=ANY(@held_grants)))
                """,parameters,ct);
            if(changed!=1)
            {
                var after=await SnapshotAsync(db,request,ct);
                return new(Error:StateError(after,account.Id)??(!ValidResultNative(after)?"persistence_unavailable":"revision_conflict"));
            }
            var row=await db.Records.AsNoTracking().SingleAsync(x=>x.Id==original.Id,ct);
            if(row.Revision!=original.Revision+1 || !Safe(row.ServerSequence) || row.ServerSequence<=original.ServerSequence
                || row.FolderId!=current.DestinationId || row.AccountId!=original.AccountId || row.EditedAt!=original.EditedAt
                || row.DeletedAt is not null || row.PurgeAt is not null || !row.Envelope.AsSpan().SequenceEqual(original.Envelope))return new(Error:"persistence_unavailable");
            foreach(var parent in changedParents)
            {
                if(await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE cerberus.folder SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp={Guid.NewGuid()}
                    WHERE id={parent.Id} AND account_id={account.Id} AND revision={parent.Revision} AND server_sequence={parent.ServerSequence}
                      AND deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=cerberus.folder.public_id)
                    """,ct)!=1)return new(Error:"revision_conflict");
                var now=await db.Folders.AsNoTracking().SingleAsync(x=>x.Id==parent.Id,ct);
                if(now.Revision!=parent.Revision+1 || !Safe(now.ServerSequence) || now.ServerSequence<=parent.ServerSequence)return new(Error:"persistence_unavailable");
            }
            await tx.CommitAsync(ct);
            return new(new(row.PublicId,request.FolderId,row.Revision,row.ServerSequence));
        }
        catch(PostgresException e) when(e.SqlState=="2200H"){return new(Error:"revision_conflict");}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return new(Error:"persistence_unavailable");}
        catch(Exception e) when(e is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            || e is InvalidOperationException {InnerException:DbException or DbUpdateException or TimeoutException}){return new(Error:"persistence_unavailable");}
    }

    private static bool Safe(long n)=>n is >0 and <=ProtocolBinary.MaxInteger;
    private static string? StateError(Snapshot s,long owner)
    {
        if(!s.ActorActive)return "not_found";if(!s.Allowed)return "vault_access_denied";
        if(s.CorruptAncestry)return "persistence_unavailable";if(s.Item is null)return "not_found";
        var item=JsonSerializer.Deserialize<Item>(s.Item,Json)!;
        if(item.AccountId!=owner)return ValidForeignNative(s)?"vault_access_denied":"persistence_unavailable";
        return s.DestinationCorrupt?"persistence_unavailable":!s.DestinationAllowed || !s.Subset?"not_found":null;
    }
    private static bool ValidResultNative(Snapshot snapshot)
    {
        var evidence=JsonSerializer.Deserialize<ResultEvidence[]>(snapshot.NativeEvidence,Json)!;
        if(evidence.Length==0)return true;
        // Parse each current pin bundle once per exact row, including recipients with
        // more than one contributing collection. Never lock a foreign account.
        var pins=new Dictionary<(string?,long?,long?,long?),ProtectionMaterial?>();
        ProtectionMaterial? Read(string? material,long? revision,long? epoch,long? generation)
        {
            var key=(material,revision,epoch,generation);
            if(pins.TryGetValue(key,out var found))return found;
            CollectionGrantBinding.TryReadPins(Pins(material,revision,epoch,generation),out found);pins[key]=found;return found;
        }
        foreach(var e in evidence)
        {
            var owner=Read(e.OwnerMaterial,e.OwnerRevision,e.OwnerEpoch,e.OwnerGeneration);
            var recipient=Read(e.RecipientMaterial,e.RecipientRevision,e.RecipientEpoch,e.RecipientGeneration);
            if(owner is null || recipient is null || !CollectionGrantBinding.IsBound(new(){PublicId=e.GrantId,Revision=e.GrantRevision,RecipientKeyEnvelope=Convert.FromHexString(e.GrantEnvelope)},
                new(){PublicId=e.CollectionId,KeyEpoch=e.CollectionEpoch,Envelope=Convert.FromHexString(e.CollectionEnvelope)},e.OwnerId,e.RecipientIdentity,owner,recipient))return false;
        }
        return true;
    }
    private static bool ValidForeignNative(Snapshot snapshot)
    {
        var grants=JsonSerializer.Deserialize<GrantEvidence[]>(snapshot.Grants,Json)!;
        if(grants.Length==0)return true;
        var p=snapshot.Protection is null?null:JsonSerializer.Deserialize<ProtectionEvidence>(snapshot.Protection,Json);
        if(p is null || !CollectionGrantBinding.TryReadPins(Pins(p.OwnerMaterial,p.OwnerRevision,p.OwnerEpoch,p.OwnerGeneration),out var owner)
            || !CollectionGrantBinding.TryReadPins(Pins(p.RecipientMaterial,p.RecipientRevision,p.RecipientEpoch,p.RecipientGeneration),out var recipient))return false;
        return grants.All(g=>CollectionGrantBinding.IsBound(new(){PublicId=g.GrantId,Revision=g.GrantRevision,RecipientKeyEnvelope=Convert.FromHexString(g.GrantEnvelope)},
            new(){PublicId=g.CollectionId,KeyEpoch=g.CollectionEpoch,Envelope=Convert.FromHexString(g.CollectionEnvelope)},p.OwnerId,p.RecipientIdentity,owner!,recipient!));
    }
    private static VaultProtection? Pins(string? material,long? revision,long? epoch,long? generation)=>material is null || revision is null || epoch is null || generation is null?null:new(){Material=Convert.FromHexString(material),Revision=revision.Value,KeyEpoch=epoch.Value,RecoveryGeneration=generation.Value};
    private static object[] Parameters(RecordMoveRequest r)=>[new NpgsqlParameter("actor",r.Actor),new NpgsqlParameter("verifier",r.AccessVerifier),new NpgsqlParameter("target",r.RecordId),new NpgsqlParameter("destination",NpgsqlDbType.Uuid){Value=(object?)r.FolderId??DBNull.Value},new NpgsqlParameter("active",(object)(int)AccountState.Active),new NpgsqlParameter("grant_active",(object)(int)CollectionGrantState.Active),new NpgsqlParameter("read_only",(object)(int)CollectionGrantAccess.ReadOnly),new NpgsqlParameter("read_write",(object)(int)CollectionGrantAccess.ReadWrite),new NpgsqlParameter("max",ProtocolBinary.MaxInteger)];
    private static async Task<Snapshot> SnapshotAsync(AppDbContext db,RecordMoveRequest request,CancellationToken ct)
        =>(await db.Database.SqlQueryRaw<Snapshot>(Authority+SnapshotSelect,Parameters(request)).ToListAsync(ct)).Single();
    private sealed record Item(Guid RecordId,long Id,long AccountId,long Revision,long Sequence,DateTimeOffset EditedAt,string Envelope);
    private sealed class Snapshot
    {
        public bool ActorActive{get;set;}public bool Allowed{get;set;}public bool CorruptAncestry{get;set;}public bool Writable{get;set;}
        public string? Item{get;set;}public long[] Collections{get;set;}=[];public long[] GrantsIds{get;set;}=[];
        public string Grants{get;set;}="[]";public string? Protection{get;set;}
        public bool DestinationAllowed{get;set;}public bool DestinationCorrupt{get;set;}public bool Subset{get;set;}
        public long? DestinationId{get;set;}public long[] SourceCollections{get;set;}=[];public long[] ResultCollections{get;set;}=[];public long[] ResultGrantIds{get;set;}=[];
        public string NativeEvidence{get;set;}="[]";
    }
    private sealed record ResultEvidence(Guid CollectionId,long CollectionEpoch,string CollectionEnvelope,Guid GrantId,long GrantRevision,string GrantEnvelope,
        Guid OwnerId,Guid RecipientIdentity,string? OwnerMaterial,long? OwnerRevision,long? OwnerEpoch,long? OwnerGeneration,
        string? RecipientMaterial,long? RecipientRevision,long? RecipientEpoch,long? RecipientGeneration);
    private sealed record GrantEvidence(Guid CollectionId,long CollectionEpoch,string CollectionEnvelope,Guid GrantId,long GrantRevision,string GrantEnvelope);
    private sealed record ProtectionEvidence(Guid OwnerId,Guid RecipientIdentity,string? OwnerMaterial,long? OwnerRevision,long? OwnerEpoch,long? OwnerGeneration,string? RecipientMaterial,long? RecipientRevision,long? RecipientEpoch,long? RecipientGeneration);
    private const string SourceSelect="""
        SELECT EXISTS(SELECT FROM actor a WHERE a.state=@active AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=a.public_id)) AS actor_active,
          EXISTS(SELECT FROM session) AS allowed,EXISTS(SELECT FROM writable) AS writable,
          EXISTS(SELECT FROM scoped r JOIN ancestry t ON t.record_id=r.id WHERE t.cycle
            AND NOT EXISTS(SELECT FROM ancestry hidden WHERE hidden.record_id=r.id AND hidden.hidden)) AS corrupt_ancestry,
          (SELECT jsonb_build_object('recordId',r.public_id,'id',r.id,'accountId',r.account_id,'revision',r.revision,
            'sequence',r.server_sequence,'editedAt',r.edited_at,'envelope',encode(r.envelope,'hex'))::text FROM visible r) AS item,
          ARRAY(SELECT DISTINCT i.collection_id FROM included i JOIN visible r ON r.id=i.record_id) AS collections,
          ARRAY(SELECT grant_id FROM relevant_grants) AS grants_ids,
          (SELECT jsonb_build_object('ownerId',owner.public_id,'recipientIdentity',a.heimdall_public_id,
            'ownerMaterial',encode(op.material,'hex'),'ownerRevision',op.revision,'ownerEpoch',op.key_epoch,'ownerGeneration',op.recovery_generation,
            'recipientMaterial',encode(rp.material,'hex'),'recipientRevision',rp.revision,'recipientEpoch',rp.key_epoch,'recipientGeneration',rp.recovery_generation)::text
            FROM visible r JOIN cerberus.account owner ON owner.id=r.account_id CROSS JOIN actor a
            LEFT JOIN cerberus.vault_protection op ON op.account_id=owner.id LEFT JOIN cerberus.vault_protection rp ON rp.account_id=a.id
            WHERE EXISTS(SELECT FROM relevant_grants)) AS protection,
          COALESCE((SELECT jsonb_agg(jsonb_build_object('collectionId',c.public_id,'collectionEpoch',c.key_epoch,
            'collectionEnvelope',encode(c.envelope,'hex'),'grantId',g.public_id,'grantRevision',g.revision,'grantEnvelope',encode(g.recipient_key_envelope,'hex')))
            FROM relevant_grants rg JOIN cerberus.collection_grant g ON g.id=rg.grant_id JOIN cerberus.collection c ON c.id=g.collection_id),'[]'::jsonb)::text AS grants
        """;
    private const string SnapshotSelect=SourceSelect+"""
        , (SELECT allowed FROM move_scope) AS destination_allowed,
          (SELECT corrupt FROM move_scope) AS destination_corrupt,
          (SELECT subset FROM move_scope) AS subset,
          (SELECT id FROM destination_visible) AS destination_id,
          ARRAY(SELECT id FROM source_collections) AS source_collections,
          ARRAY(SELECT id FROM result_collections) AS result_collections,
          ARRAY(SELECT id FROM result_grants) AS result_grant_ids,
          (SELECT evidence::text FROM native_result) AS native_evidence
        """;
    private const string SourceAuthority="""
        WITH RECURSIVE actor AS (
                            SELECT a.* FROM cerberus.account a WHERE a.heimdall_public_id=@actor
                        ), session AS (
                            SELECT s.* FROM cerberus.vault_access_session s JOIN actor a ON a.id=s.account_id
                            WHERE a.state=@active
                              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=a.public_id)
                              AND s.handle_verifier=@verifier AND NOT s.revoked
                              AND s.policy_revision>0 AND s.revocation_generation>0
                              AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation
                              AND s.issued_at<=statement_timestamp()
                              AND CASE WHEN a.renewal_enabled THEN s.expires_at>statement_timestamp() ELSE s.expires_at IS NULL END
                              AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile p
                                  WHERE p.id=s.profile_id AND p.account_id=a.id AND p.deleted_at IS NULL
                                    AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=p.public_id)))
                        ), collections AS (
                            SELECT c.*,g.id grant_id FROM cerberus.collection c
                            JOIN cerberus.account owner ON owner.id=c.account_id CROSS JOIN session s
                            LEFT JOIN cerberus.collection_grant g ON g.collection_id=c.id AND g.recipient_account_id=s.account_id
                              AND g.state=@grant_active
                              AND g.access IN (@read_only,@read_write)
                              AND g.revision>0 AND g.revision<=@max
                              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=g.public_id)
                            WHERE owner.state=@active AND c.deleted_at IS NULL
                              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=owner.public_id OR e.resource_id=c.public_id)
                              AND (c.account_id=s.account_id OR g.id IS NOT NULL)
                              AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_collection pc WHERE pc.profile_id=s.profile_id AND pc.collection_id=c.id))
                        ), candidates AS MATERIALIZED (
                            SELECT r.* FROM cerberus.record r JOIN cerberus.account owner ON owner.id=r.account_id CROSS JOIN session s
                            WHERE r.public_id=@target AND owner.state=@active
                              AND (r.account_id=s.account_id OR EXISTS(SELECT FROM collections c WHERE c.account_id=r.account_id))
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
                        ), relevant_grants AS (
                            SELECT DISTINCT i.grant_id FROM included i JOIN visible r ON r.id=i.record_id CROSS JOIN session s
                            WHERE r.account_id<>s.account_id AND i.grant_id IS NOT NULL
                        ), writable AS (
            SELECT r.* FROM visible r CROSS JOIN session s WHERE r.account_id=s.account_id
              OR EXISTS(SELECT FROM included i JOIN cerberus.collection_grant g ON g.id=i.grant_id
                        WHERE i.record_id=r.id AND g.access=@read_write)
        )
        """;
    private const string Authority=SourceAuthority+MoveAuthority;
    private const string MoveAuthority="""
        , own_collections AS (
            SELECT c.* FROM cerberus.collection c CROSS JOIN actor a
            WHERE c.account_id=a.id AND a.state=@active AND c.deleted_at IS NULL
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=c.public_id)
        ), destination_candidate AS MATERIALIZED (
            SELECT f.* FROM cerberus.folder f CROSS JOIN actor a
            WHERE f.public_id=@destination AND f.account_id=a.id
        ), destination_ancestry AS (
            SELECT f.id,f.account_id,f.parent_folder_id,ARRAY[f.id] path,false cycle,
              (f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=f.public_id)) hidden
            FROM destination_candidate f
            UNION ALL
            SELECT f.id,f.account_id,f.parent_folder_id,t.path||f.id,f.id=ANY(t.path),
              t.hidden OR f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=f.public_id)
            FROM destination_ancestry t JOIN cerberus.folder f ON f.id=t.parent_folder_id AND f.account_id=t.account_id WHERE NOT t.cycle
        ), destination_collections AS (
            SELECT c.id FROM own_collections c WHERE EXISTS(SELECT FROM cerberus.collection_folder cf
              JOIN destination_ancestry t ON t.id=cf.folder_id WHERE cf.account_id=c.account_id AND cf.collection_id=c.id)
        ), destination_scoped AS (
            SELECT f.* FROM destination_candidate f CROSS JOIN session s
            WHERE s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN destination_ancestry t ON t.id=pf.folder_id
              WHERE pf.account_id=s.account_id AND pf.profile_id=s.profile_id)
              OR EXISTS(SELECT FROM cerberus.profile_collection pc JOIN destination_collections c ON c.id=pc.collection_id WHERE pc.profile_id=s.profile_id)
        ), destination_visible AS (
            SELECT f.* FROM destination_scoped f WHERE NOT EXISTS(SELECT FROM destination_ancestry t WHERE t.hidden OR t.cycle)
              AND EXISTS(SELECT FROM destination_ancestry t WHERE t.parent_folder_id IS NULL)
        ), source_collections AS (
            SELECT c.id FROM own_collections c JOIN visible r ON r.account_id=c.account_id
            WHERE EXISTS(SELECT FROM cerberus.collection_record cr WHERE cr.collection_id=c.id AND cr.record_id=r.id AND cr.account_id=c.account_id)
              OR EXISTS(SELECT FROM cerberus.collection_folder cf JOIN ancestry t ON t.id=cf.folder_id
                WHERE cf.collection_id=c.id AND cf.account_id=c.account_id AND t.record_id=r.id)
        ), result_collections AS (
            SELECT c.id FROM own_collections c JOIN visible r ON r.account_id=c.account_id
            WHERE EXISTS(SELECT FROM cerberus.collection_record cr WHERE cr.collection_id=c.id AND cr.record_id=r.id AND cr.account_id=c.account_id)
              OR EXISTS(SELECT FROM destination_collections dc WHERE dc.id=c.id)
        ), own_profiles AS (
            SELECT p.* FROM cerberus.profile p CROSS JOIN actor a WHERE p.account_id=a.id AND p.deleted_at IS NULL
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=p.public_id)
        ), source_profiles AS (
            SELECT p.id FROM own_profiles p JOIN visible r ON r.account_id=p.account_id
            WHERE EXISTS(SELECT FROM cerberus.profile_record pr WHERE pr.profile_id=p.id AND pr.record_id=r.id AND pr.account_id=p.account_id)
              OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN ancestry t ON t.id=pf.folder_id
                WHERE pf.profile_id=p.id AND pf.account_id=p.account_id AND t.record_id=r.id)
              OR EXISTS(SELECT FROM cerberus.profile_collection pc JOIN source_collections c ON c.id=pc.collection_id WHERE pc.profile_id=p.id)
        ), result_profiles AS (
            SELECT p.id FROM own_profiles p JOIN visible r ON r.account_id=p.account_id
            WHERE EXISTS(SELECT FROM cerberus.profile_record pr WHERE pr.profile_id=p.id AND pr.record_id=r.id AND pr.account_id=p.account_id)
              OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN destination_ancestry t ON t.id=pf.folder_id
                WHERE pf.profile_id=p.id AND pf.account_id=p.account_id)
              OR EXISTS(SELECT FROM cerberus.profile_collection pc JOIN result_collections c ON c.id=pc.collection_id WHERE pc.profile_id=p.id)
        ), move_scope AS (
            SELECT (@destination IS NULL OR EXISTS(SELECT FROM destination_visible)) allowed,
              (EXISTS(SELECT FROM destination_scoped) AND EXISTS(SELECT FROM destination_ancestry WHERE cycle)
                AND NOT EXISTS(SELECT FROM destination_ancestry WHERE hidden)) corrupt,
              (EXISTS(SELECT FROM session WHERE profile_id IS NULL) OR
                (NOT EXISTS(SELECT id FROM result_collections EXCEPT SELECT id FROM source_collections)
                  AND NOT EXISTS(SELECT id FROM result_profiles EXCEPT SELECT id FROM source_profiles))) subset
        ), result_grants AS (
            SELECT g.* FROM cerberus.collection_grant g JOIN result_collections c ON c.id=g.collection_id
            JOIN cerberus.account recipient ON recipient.id=g.recipient_account_id
            WHERE g.state=@grant_active AND g.access IN (@read_only,@read_write) AND recipient.state=@active
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=g.public_id OR e.resource_id=recipient.public_id)
        ), native_result AS (
            SELECT COALESCE(jsonb_agg(jsonb_build_object('collectionId',c.public_id,'collectionEpoch',c.key_epoch,'collectionEnvelope',encode(c.envelope,'hex'),
              'grantId',g.public_id,'grantRevision',g.revision,'grantEnvelope',encode(g.recipient_key_envelope,'hex'),
              'ownerId',a.public_id,'recipientIdentity',recipient.heimdall_public_id,
              'ownerMaterial',encode(op.material,'hex'),'ownerRevision',op.revision,'ownerEpoch',op.key_epoch,'ownerGeneration',op.recovery_generation,
              'recipientMaterial',encode(rp.material,'hex'),'recipientRevision',rp.revision,'recipientEpoch',rp.key_epoch,'recipientGeneration',rp.recovery_generation)
              ORDER BY g.public_id),'[]'::jsonb) evidence
            FROM result_grants g JOIN cerberus.collection c ON c.id=g.collection_id CROSS JOIN actor a
            JOIN cerberus.account recipient ON recipient.id=g.recipient_account_id
            LEFT JOIN cerberus.vault_protection op ON op.account_id=a.id LEFT JOIN cerberus.vault_protection rp ON rp.account_id=recipient.id
        )
        """;
}
