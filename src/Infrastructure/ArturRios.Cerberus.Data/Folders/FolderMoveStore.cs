using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
namespace ArturRios.Cerberus.Data.Folders;

public sealed class FolderMoveStore(IDbContextFactory<AppDbContext> factory):IFolderMoveStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {PropertyNameCaseInsensitive=false,AllowDuplicateProperties=false,NumberHandling=JsonNumberHandling.Strict};

    public async Task<VaultResult<FolderMoveDetails>> MoveAsync(FolderMoveRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        if(request.FolderId==Guid.Empty || request.ParentFolderId==Guid.Empty || !Safe(request.ExpectedRevision))return new(Error:"validation_failed");
        try
        {
            await using var db=await factory.CreateDbContextAsync(ct);
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            // Account UPDATE also serializes new owned entity FK references. The
            // collection-before-content order below matches recipient content writers.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR UPDATE",ct);
            var account=await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.HeimdallPublicId==request.Actor,ct);
            if(account is null || account.State!=AccountState.Active || await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account" && x.ResourceId==account.PublicId,ct))return new(Error:"not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE",ct);
            var initial=await SnapshotAsync(db,request,ct);var error=StateError(initial,account.Id);
            if(error is not null)return new(Error:error);
            var item=JsonSerializer.Deserialize<Item>(initial.Item!,Json)!;
            var original=await db.Folders.AsNoTracking().SingleAsync(x=>x.Id==item.Id,ct);
            var originalParent=original.ParentFolderId;
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
            var inventory=JsonSerializer.Deserialize<Member[]>(initial.Members,Json)!;
            var capturedFolders=inventory.Where(x=>x.Kind=="folder").Select(x=>x.Id).ToArray();
            var capturedRecords=inventory.Where(x=>x.Kind=="record").Select(x=>x.Id).ToArray();
            var folderIds=capturedFolders.Concat(new[]{originalParent,initial.DestinationId}.OfType<long>()).Distinct().ToArray();
            var folders=await db.Folders.FromSqlInterpolated($"""
                SELECT f.* FROM cerberus.folder f WHERE f.account_id={account.Id} AND f.id=ANY({folderIds}) ORDER BY f.public_id FOR UPDATE
                """).AsNoTracking().ToListAsync(ct);
            error=StateError(await SnapshotAsync(db,request,ct),account.Id);if(error is not null)return new(Error:error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.record WHERE account_id={account.Id} AND id=ANY({capturedRecords}) ORDER BY public_id FOR UPDATE",ct);
            error=StateError(await SnapshotAsync(db,request,ct),account.Id);if(error is not null)return new(Error:error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile_folder WHERE account_id={account.Id} AND folder_id=ANY({capturedFolders}) ORDER BY profile_id,folder_id FOR UPDATE",ct);
            error=StateError(await SnapshotAsync(db,request,ct),account.Id);if(error is not null)return new(Error:error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile_record WHERE account_id={account.Id} AND record_id=ANY({capturedRecords}) ORDER BY profile_id,record_id FOR UPDATE",ct);
            error=StateError(await SnapshotAsync(db,request,ct),account.Id);if(error is not null)return new(Error:error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection_folder WHERE account_id={account.Id} AND folder_id=ANY({capturedFolders}) ORDER BY collection_id,folder_id FOR UPDATE",ct);
            error=StateError(await SnapshotAsync(db,request,ct),account.Id);if(error is not null)return new(Error:error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection_record WHERE account_id={account.Id} AND record_id=ANY({capturedRecords}) ORDER BY collection_id,record_id FOR UPDATE",ct);
            var current=await SnapshotAsync(db,request,ct);error=StateError(current,account.Id);if(error is not null)return new(Error:error);
            if(!ValidResultNative(current))return new(Error:"persistence_unavailable");
            var heldCollections=collections.Select(x=>x.Id).ToArray();var heldGrants=grants.Select(x=>x.Id).ToArray();
            if(current.Frozen!=initial.Frozen || current.SourceCollections.Concat(current.ResultCollections).Except(heldCollections).Any()
                || current.ResultGrantIds.Except(heldGrants).Any() || current.DestinationId!=initial.DestinationId)return new(Error:"revision_conflict");
            original=await db.Folders.AsNoTracking().SingleAsync(x=>x.Id==item.Id,ct);
            if(!ValidMetadata(original.Revision,original.ServerSequence,original.EditedAt,original.PurgeAt)
                || inventory.Any(x=>!ValidMetadata(x.Revision,x.Sequence,x.EditedAt,x.PurgeAt))
                || JsonSerializer.Deserialize<EncryptedEnvelope>(original.Envelope,Json)?.IsValid()!=true)return new(Error:"persistence_unavailable");
            if(original.Revision!=request.ExpectedRevision || original.Revision==ProtocolBinary.MaxInteger)return new(Error:"revision_conflict");
            var changedParents=originalParent!=current.DestinationId?folders.Where(x=>x.Id==originalParent || x.Id==current.DestinationId).ToArray():[];
            if(changedParents.Any(x=>!ValidMetadata(x.Revision,x.ServerSequence,x.EditedAt,x.PurgeAt) || x.DeletedAt is not null))return new(Error:"persistence_unavailable");
            if(changedParents.Any(x=>x.Revision==ProtocolBinary.MaxInteger))return new(Error:"revision_conflict");
            var parameters=Parameters(request).Concat(new object[]{new NpgsqlParameter("expected",request.ExpectedRevision),
                new NpgsqlParameter("previous_sequence",original.ServerSequence),new NpgsqlParameter("original",original.Envelope),
                new NpgsqlParameter("parent",NpgsqlDbType.Bigint){Value=(object?)originalParent??DBNull.Value},
                new NpgsqlParameter("held_collections",heldCollections),new NpgsqlParameter("held_grants",heldGrants),
                new NpgsqlParameter("frozen",NpgsqlDbType.Jsonb){Value=current.Frozen},new NpgsqlParameter("stamp",Guid.NewGuid())}).ToArray();
            var changed=await db.Database.ExecuteSqlRawAsync(Authority+"""
                UPDATE cerberus.folder AS root SET parent_folder_id=(SELECT id FROM destination_visible),revision=root.revision+1,
                  server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp
                FROM visible v CROSS JOIN session s CROSS JOIN move_scope m CROSS JOIN captured snapshot
                WHERE root.id=v.id AND root.account_id=s.account_id AND root.public_id=@target
                  AND m.allowed AND NOT m.corrupt AND NOT m.invalid_cycle AND m.subset AND snapshot.evidence=@frozen
                  AND root.revision=@expected AND v.revision=@expected AND v.server_sequence=@previous_sequence
                  AND v.envelope=@original AND v.parent_folder_id IS NOT DISTINCT FROM @parent AND root.purge_at IS NULL
                  AND NOT EXISTS(SELECT FROM source_collections c WHERE NOT(c.id=ANY(@held_collections)))
                  AND NOT EXISTS(SELECT FROM result_collections c WHERE NOT(c.id=ANY(@held_collections)))
                  AND NOT EXISTS(SELECT FROM result_grants g WHERE NOT(g.id=ANY(@held_grants)))
                """,parameters,ct);
            if(changed!=1)
            {
                var after=await SnapshotAsync(db,request,ct);
                return new(Error:StateError(after,account.Id)??(!ValidResultNative(after)?"persistence_unavailable":"revision_conflict"));
            }
            var row=await db.Folders.AsNoTracking().SingleAsync(x=>x.Id==original.Id,ct);
            if(row.PublicId!=original.PublicId || row.Revision!=original.Revision+1 || !Safe(row.ServerSequence) || row.ServerSequence<=original.ServerSequence
                || row.ParentFolderId!=current.DestinationId || row.AccountId!=original.AccountId || row.EditedAt!=original.EditedAt
                || row.DeletedAt is not null || row.PurgeAt is not null || !row.Envelope.AsSpan().SequenceEqual(original.Envelope))return new(Error:"persistence_unavailable");
            foreach(var parent in changedParents)
            {
                if(await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE cerberus.folder SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp={Guid.NewGuid()}
                    WHERE id={parent.Id} AND account_id={account.Id} AND revision={parent.Revision} AND server_sequence={parent.ServerSequence}
                      AND deleted_at IS NULL AND purge_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=cerberus.folder.public_id)
                    """,ct)!=1)return new(Error:"revision_conflict");
                var now=await db.Folders.AsNoTracking().SingleAsync(x=>x.Id==parent.Id,ct);
                if(now.Revision!=parent.Revision+1 || !Safe(now.ServerSequence) || now.ServerSequence<=parent.ServerSequence
                    || now.PublicId!=parent.PublicId || now.AccountId!=parent.AccountId || now.ParentFolderId!=parent.ParentFolderId
                    || now.EditedAt!=parent.EditedAt || now.DeletedAt is not null || now.PurgeAt is not null
                    || !now.Envelope.AsSpan().SequenceEqual(parent.Envelope))return new(Error:"persistence_unavailable");
            }
            await tx.CommitAsync(ct);
            return new(new(row.PublicId,request.ParentFolderId,row.Revision,row.ServerSequence));
        }
        catch(PostgresException e) when(e.SqlState=="2200H"){return new(Error:"revision_conflict");}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return new(Error:"persistence_unavailable");}
        catch(Exception e) when(e is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            || e is InvalidOperationException {InnerException:DbException or DbUpdateException or TimeoutException}){return new(Error:"persistence_unavailable");}
    }
    private static bool Safe(long n)=>n is >0 and <=ProtocolBinary.MaxInteger;
    private static bool ValidMetadata(long revision,long sequence,DateTimeOffset editedAt,DateTimeOffset? purgeAt)
        =>Safe(revision) && Safe(sequence) && editedAt.Offset==TimeSpan.Zero && editedAt.Ticks>=10 && editedAt.Ticks%10==0 && purgeAt is null;
    private static string? StateError(Snapshot s,long owner)
    {
        if(!s.ActorActive)return "not_found";if(!s.Allowed)return "vault_access_denied";
        if(s.CorruptAncestry)return "persistence_unavailable";if(s.Item is null)return "not_found";
        if(JsonSerializer.Deserialize<Item>(s.Item,Json)!.AccountId!=owner)return ValidForeignNative(s)?"vault_access_denied":"persistence_unavailable";
        if(s.DestinationCorrupt || s.MemberCorrupt)return "persistence_unavailable";
        if(!s.DestinationAllowed)return "not_found";if(s.InvalidCycle)return "validation_failed";
        return !s.Subset?"not_found":null;
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
    private static object[] Parameters(FolderMoveRequest r)=>[new NpgsqlParameter("actor",r.Actor),new NpgsqlParameter("verifier",r.AccessVerifier),new NpgsqlParameter("target",r.FolderId),new NpgsqlParameter("destination",NpgsqlDbType.Uuid){Value=(object?)r.ParentFolderId??DBNull.Value},new NpgsqlParameter("active",(object)(int)AccountState.Active),new NpgsqlParameter("grant_active",(object)(int)CollectionGrantState.Active),new NpgsqlParameter("read_only",(object)(int)CollectionGrantAccess.ReadOnly),new NpgsqlParameter("read_write",(object)(int)CollectionGrantAccess.ReadWrite),new NpgsqlParameter("max",ProtocolBinary.MaxInteger)];
    private static async Task<Snapshot> SnapshotAsync(AppDbContext db,FolderMoveRequest request,CancellationToken ct)
        =>(await db.Database.SqlQueryRaw<Snapshot>(Authority+SnapshotSelect,Parameters(request)).ToListAsync(ct)).Single();
    private sealed record Item(Guid FolderId,long Id,long AccountId,long Revision,long Sequence,DateTimeOffset EditedAt,string Envelope);
    private sealed record Member(string Kind,long Id,long Revision,long Sequence,DateTimeOffset EditedAt,DateTimeOffset? PurgeAt);
    private sealed class Snapshot
    {
        public bool ActorActive{get;set;}public bool Allowed{get;set;}public bool CorruptAncestry{get;set;}public bool Writable{get;set;}
        public string? Item{get;set;}public long[] Collections{get;set;}=[];public long[] GrantsIds{get;set;}=[];
        public string Grants{get;set;}="[]";public string? Protection{get;set;}
        public bool DestinationAllowed{get;set;}public bool DestinationCorrupt{get;set;}public bool InvalidCycle{get;set;}public bool MemberCorrupt{get;set;}public bool Subset{get;set;}
        public long? DestinationId{get;set;}public long[] SourceCollections{get;set;}=[];public long[] ResultCollections{get;set;}=[];public long[] ResultGrantIds{get;set;}=[];
        public string NativeEvidence{get;set;}="[]";public string Members{get;set;}="[]";public string Frozen{get;set;}="{}";
    }
    private sealed record ResultEvidence(Guid CollectionId,long CollectionEpoch,string CollectionEnvelope,Guid GrantId,long GrantRevision,string GrantEnvelope,
        Guid OwnerId,Guid RecipientIdentity,string? OwnerMaterial,long? OwnerRevision,long? OwnerEpoch,long? OwnerGeneration,
        string? RecipientMaterial,long? RecipientRevision,long? RecipientEpoch,long? RecipientGeneration);
    private sealed record GrantEvidence(Guid CollectionId,long CollectionEpoch,string CollectionEnvelope,Guid GrantId,long GrantRevision,string GrantEnvelope);
    private sealed record ProtectionEvidence(Guid OwnerId,Guid RecipientIdentity,string? OwnerMaterial,long? OwnerRevision,long? OwnerEpoch,long? OwnerGeneration,string? RecipientMaterial,long? RecipientRevision,long? RecipientEpoch,long? RecipientGeneration);
    private const string SourceSelect="""
        SELECT EXISTS(SELECT FROM actor a WHERE a.state=@active AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='account' AND e.resource_id=a.public_id))) AS actor_active,
          EXISTS(SELECT FROM session) AS allowed,EXISTS(SELECT FROM writable) AS writable,
          EXISTS(SELECT FROM scoped r JOIN ancestry t ON t.folder_id=r.id WHERE t.cycle
            AND NOT EXISTS(SELECT FROM ancestry hidden WHERE hidden.folder_id=r.id AND hidden.hidden)) AS corrupt_ancestry,
          (SELECT jsonb_build_object('folderId',r.public_id,'id',r.id,'accountId',r.account_id,'revision',r.revision,
            'sequence',r.server_sequence,'editedAt',r.edited_at,'envelope',encode(r.envelope,'hex'))::text FROM visible r) AS item,
          ARRAY(SELECT DISTINCT i.collection_id FROM included i JOIN visible r ON r.id=i.folder_id) AS collections,
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
        ,m.allowed destination_allowed,m.corrupt destination_corrupt,m.invalid_cycle,m.subset,
          (EXISTS(SELECT FROM tree WHERE cycle) OR EXISTS(SELECT FROM before_paths WHERE cycle OR hidden)
            OR (NOT m.invalid_cycle AND EXISTS(SELECT FROM after_paths WHERE cycle OR hidden))) member_corrupt,
          (SELECT id FROM destination_visible) destination_id,
          ARRAY(SELECT id FROM source_collections ORDER BY id) source_collections,
          ARRAY(SELECT id FROM result_collections ORDER BY id) result_collections,
          ARRAY(SELECT id FROM result_grants ORDER BY public_id) result_grant_ids,
          n.evidence::text native_evidence,snapshot.evidence::text frozen,
          COALESCE((SELECT jsonb_agg(jsonb_build_object('kind',r.kind,'id',r.id,'revision',r.revision,'sequence',r.server_sequence,
            'editedAt',r.edited_at,'purgeAt',r.purge_at) ORDER BY r.kind,r.public_id) FROM members r),'[]'::jsonb)::text members
        FROM move_scope m CROSS JOIN native_result n CROSS JOIN captured snapshot
        """;
    private const string SourceAuthority="""
        WITH RECURSIVE actor AS (
                            SELECT a.* FROM cerberus.account a WHERE a.heimdall_public_id=@actor
                        ), session AS (
                            SELECT s.* FROM cerberus.vault_access_session s JOIN actor a ON a.id=s.account_id
                            WHERE a.state=@active
                              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='account' AND e.resource_id=a.public_id))
                              AND s.handle_verifier=@verifier AND NOT s.revoked
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
                              AND g.state=@grant_active
                              AND g.access IN (@read_only,@read_write)
                              AND g.revision>0 AND g.revision<=@max
                              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='grant' AND e.resource_id=g.public_id))
                            WHERE owner.state=@active AND c.deleted_at IS NULL
                              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='account' AND e.resource_id=owner.public_id) OR (e.resource_kind='collection' AND e.resource_id=c.public_id))
                              AND (c.account_id=s.account_id OR g.id IS NOT NULL)
                              AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile_collection pc WHERE pc.profile_id=s.profile_id AND pc.collection_id=c.id))
                        ), candidates AS MATERIALIZED (
                            SELECT r.* FROM cerberus.folder r JOIN cerberus.account owner ON owner.id=r.account_id CROSS JOIN session s
                            WHERE r.public_id=@target AND owner.state=@active
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
                        ), writable AS (
            SELECT r.* FROM visible r CROSS JOIN session s WHERE r.account_id=s.account_id
              OR EXISTS(SELECT FROM included i JOIN cerberus.collection_grant g ON g.id=i.grant_id
                        WHERE i.folder_id=r.id AND g.access=@read_write)
        )
        """;
    private const string Authority=SourceAuthority+MoveAuthority;
    private const string MoveAuthority="""
        , own_collections AS (
            SELECT c.* FROM cerberus.collection c CROSS JOIN actor a WHERE c.account_id=a.id AND a.state=@active AND c.deleted_at IS NULL
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='collection' AND e.resource_id=c.public_id)
        ), destination_candidate AS MATERIALIZED (
            SELECT f.* FROM cerberus.folder f CROSS JOIN actor a WHERE f.public_id=@destination AND f.account_id=a.id
        ), destination_ancestry AS (
            SELECT f.id,f.account_id,f.parent_folder_id,ARRAY[f.id] path,false cycle,
              (f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)) hidden
            FROM destination_candidate f
            UNION ALL
            SELECT f.id,f.account_id,f.parent_folder_id,t.path||f.id,f.id=ANY(t.path),
              t.hidden OR f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
            FROM destination_ancestry t JOIN cerberus.folder f ON f.id=t.parent_folder_id AND f.account_id=t.account_id WHERE NOT t.cycle
        ), destination_collections AS (
            SELECT c.id FROM own_collections c WHERE EXISTS(SELECT FROM cerberus.collection_folder cf JOIN destination_ancestry t ON t.id=cf.folder_id
              WHERE cf.account_id=c.account_id AND cf.collection_id=c.id)
        ), destination_scoped AS (
            SELECT f.* FROM destination_candidate f CROSS JOIN session s WHERE s.profile_id IS NULL
              OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN destination_ancestry t ON t.id=pf.folder_id WHERE pf.account_id=s.account_id AND pf.profile_id=s.profile_id)
              OR EXISTS(SELECT FROM cerberus.profile_collection pc JOIN destination_collections c ON c.id=pc.collection_id WHERE pc.profile_id=s.profile_id)
        ), destination_visible AS (
            SELECT f.* FROM destination_scoped f WHERE NOT EXISTS(SELECT FROM destination_ancestry t WHERE t.hidden OR t.cycle)
              AND EXISTS(SELECT FROM destination_ancestry t WHERE t.parent_folder_id IS NULL)
        ), tree AS (
            SELECT f.*,ARRAY[f.id] path,false cycle FROM visible f CROSS JOIN actor a WHERE f.account_id=a.id
            UNION ALL
            SELECT f.*,t.path||f.id,f.id=ANY(t.path) FROM tree t JOIN cerberus.folder f ON f.account_id=t.account_id AND f.parent_folder_id=t.id
            WHERE NOT t.cycle AND f.deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
        ), members AS MATERIALIZED (
            SELECT 'folder'::text kind,t.id,t.public_id,t.account_id,t.parent_folder_id parent,t.envelope,t.edited_at,t.revision,t.server_sequence,t.concurrency_stamp,t.purge_at
            FROM tree t WHERE NOT t.cycle
            UNION ALL
            SELECT 'record'::text kind,r.id,r.public_id,r.account_id,r.folder_id parent,r.envelope,r.edited_at,r.revision,r.server_sequence,r.concurrency_stamp,r.purge_at
            FROM cerberus.record r JOIN tree t ON t.id=r.folder_id AND t.account_id=r.account_id WHERE NOT t.cycle AND r.deleted_at IS NULL
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='record' AND e.resource_id=r.public_id)
        ), before_paths AS (
            SELECT m.kind,m.id member_id,m.account_id,f.id,f.parent_folder_id,ARRAY[f.id] path,false cycle,
              (f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)) hidden
            FROM members m JOIN cerberus.folder f ON f.id=CASE WHEN m.kind='folder' THEN m.id ELSE m.parent END AND f.account_id=m.account_id
            UNION ALL
            SELECT t.kind,t.member_id,t.account_id,f.id,f.parent_folder_id,t.path||f.id,f.id=ANY(t.path),
              t.hidden OR f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
            FROM before_paths t JOIN cerberus.folder f ON f.id=t.parent_folder_id AND f.account_id=t.account_id WHERE NOT t.cycle
        ), after_paths AS (
            SELECT m.kind,m.id member_id,m.account_id,f.id,CASE WHEN f.id=(SELECT id FROM visible) THEN (SELECT id FROM destination_visible) ELSE f.parent_folder_id END parent_folder_id,
              ARRAY[f.id] path,false cycle,(f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)) hidden
            FROM members m JOIN cerberus.folder f ON f.id=CASE WHEN m.kind='folder' THEN m.id ELSE m.parent END AND f.account_id=m.account_id
            UNION ALL
            SELECT t.kind,t.member_id,t.account_id,f.id,CASE WHEN f.id=(SELECT id FROM visible) THEN (SELECT id FROM destination_visible) ELSE f.parent_folder_id END,
              t.path||f.id,f.id=ANY(t.path),t.hidden OR f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
            FROM after_paths t JOIN cerberus.folder f ON f.id=t.parent_folder_id AND f.account_id=t.account_id WHERE NOT t.cycle
        ), own_profiles AS (
            SELECT p.* FROM cerberus.profile p CROSS JOIN actor a WHERE p.account_id=a.id AND p.deleted_at IS NULL
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id)
        ), before_collections AS (
            SELECT m.kind,m.id member_id,c.id FROM members m JOIN own_collections c ON c.account_id=m.account_id
            WHERE EXISTS(SELECT FROM cerberus.collection_folder cf JOIN before_paths t ON t.id=cf.folder_id
              WHERE cf.account_id=c.account_id AND cf.collection_id=c.id AND t.kind=m.kind AND t.member_id=m.id)
              OR (m.kind='record' AND EXISTS(SELECT FROM cerberus.collection_record cr WHERE cr.account_id=c.account_id AND cr.collection_id=c.id AND cr.record_id=m.id))
        ), after_collections AS (
            SELECT m.kind,m.id member_id,c.id FROM members m JOIN own_collections c ON c.account_id=m.account_id
            WHERE EXISTS(SELECT FROM cerberus.collection_folder cf JOIN after_paths t ON t.id=cf.folder_id
              WHERE cf.account_id=c.account_id AND cf.collection_id=c.id AND t.kind=m.kind AND t.member_id=m.id)
              OR (m.kind='record' AND EXISTS(SELECT FROM cerberus.collection_record cr WHERE cr.account_id=c.account_id AND cr.collection_id=c.id AND cr.record_id=m.id))
        ), before_profiles AS (
            SELECT m.kind,m.id member_id,p.id FROM members m JOIN own_profiles p ON p.account_id=m.account_id
            WHERE EXISTS(SELECT FROM cerberus.profile_folder pf JOIN before_paths t ON t.id=pf.folder_id
              WHERE pf.account_id=p.account_id AND pf.profile_id=p.id AND t.kind=m.kind AND t.member_id=m.id)
              OR (m.kind='record' AND EXISTS(SELECT FROM cerberus.profile_record pr WHERE pr.account_id=p.account_id AND pr.profile_id=p.id AND pr.record_id=m.id))
              OR EXISTS(SELECT FROM cerberus.profile_collection pc JOIN before_collections c ON c.id=pc.collection_id WHERE pc.profile_id=p.id AND c.kind=m.kind AND c.member_id=m.id)
        ), after_profiles AS (
            SELECT m.kind,m.id member_id,p.id FROM members m JOIN own_profiles p ON p.account_id=m.account_id
            WHERE EXISTS(SELECT FROM cerberus.profile_folder pf JOIN after_paths t ON t.id=pf.folder_id
              WHERE pf.account_id=p.account_id AND pf.profile_id=p.id AND t.kind=m.kind AND t.member_id=m.id)
              OR (m.kind='record' AND EXISTS(SELECT FROM cerberus.profile_record pr WHERE pr.account_id=p.account_id AND pr.profile_id=p.id AND pr.record_id=m.id))
              OR EXISTS(SELECT FROM cerberus.profile_collection pc JOIN after_collections c ON c.id=pc.collection_id WHERE pc.profile_id=p.id AND c.kind=m.kind AND c.member_id=m.id)
        ), source_collections AS (SELECT DISTINCT id FROM before_collections), result_collections AS (SELECT DISTINCT id FROM after_collections),
        move_scope AS (
            SELECT (@destination IS NULL OR EXISTS(SELECT FROM destination_visible)) allowed,
              (EXISTS(SELECT FROM destination_scoped) AND EXISTS(SELECT FROM destination_ancestry WHERE cycle) AND NOT EXISTS(SELECT FROM destination_ancestry WHERE hidden)) corrupt,
              EXISTS(SELECT FROM destination_visible f JOIN tree t ON t.id=f.id) invalid_cycle,
              (EXISTS(SELECT FROM session WHERE profile_id IS NULL) OR
                (NOT EXISTS(SELECT kind,member_id,id FROM after_collections EXCEPT SELECT kind,member_id,id FROM before_collections)
                  AND NOT EXISTS(SELECT kind,member_id,id FROM after_profiles EXCEPT SELECT kind,member_id,id FROM before_profiles))) subset
        ), result_grants AS (
            SELECT g.* FROM cerberus.collection_grant g JOIN result_collections c ON c.id=g.collection_id JOIN cerberus.account recipient ON recipient.id=g.recipient_account_id
            WHERE g.state=@grant_active AND g.access IN (@read_only,@read_write) AND recipient.state=@active
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='grant' AND e.resource_id=g.public_id) OR (e.resource_kind='account' AND e.resource_id=recipient.public_id))
        ), native_result AS (
            SELECT COALESCE(jsonb_agg(jsonb_build_object('collectionId',c.public_id,'collectionEpoch',c.key_epoch,'collectionEnvelope',encode(c.envelope,'hex'),
              'grantId',g.public_id,'grantRevision',g.revision,'grantEnvelope',encode(g.recipient_key_envelope,'hex'),
              'ownerId',a.public_id,'recipientIdentity',recipient.heimdall_public_id,
              'ownerMaterial',encode(op.material,'hex'),'ownerRevision',op.revision,'ownerEpoch',op.key_epoch,'ownerGeneration',op.recovery_generation,
              'recipientMaterial',encode(rp.material,'hex'),'recipientRevision',rp.revision,'recipientEpoch',rp.key_epoch,'recipientGeneration',rp.recovery_generation)
              ORDER BY g.public_id),'[]'::jsonb) evidence
            FROM result_grants g JOIN cerberus.collection c ON c.id=g.collection_id CROSS JOIN actor a JOIN cerberus.account recipient ON recipient.id=g.recipient_account_id
            LEFT JOIN cerberus.vault_protection op ON op.account_id=a.id LEFT JOIN cerberus.vault_protection rp ON rp.account_id=recipient.id
        ), captured AS (
            SELECT jsonb_build_object(
              'members',COALESCE((SELECT jsonb_agg(to_jsonb(m) ORDER BY m.kind,m.public_id) FROM members m),'[]'::jsonb),
              'beforePaths',COALESCE((SELECT jsonb_agg(to_jsonb(t) ORDER BY t.kind,t.member_id,t.id) FROM before_paths t),'[]'::jsonb),
              'afterPaths',COALESCE((SELECT jsonb_agg(to_jsonb(t) ORDER BY t.kind,t.member_id,t.id) FROM after_paths t),'[]'::jsonb),
              'beforeCollections',COALESCE((SELECT jsonb_agg(to_jsonb(c) ORDER BY c.kind,c.member_id,c.id) FROM before_collections c),'[]'::jsonb),
              'afterCollections',COALESCE((SELECT jsonb_agg(to_jsonb(c) ORDER BY c.kind,c.member_id,c.id) FROM after_collections c),'[]'::jsonb),
              'beforeProfiles',COALESCE((SELECT jsonb_agg(to_jsonb(p) ORDER BY p.kind,p.member_id,p.id) FROM before_profiles p),'[]'::jsonb),
              'afterProfiles',COALESCE((SELECT jsonb_agg(to_jsonb(p) ORDER BY p.kind,p.member_id,p.id) FROM after_profiles p),'[]'::jsonb),
              'profiles',COALESCE((SELECT jsonb_agg(to_jsonb(p) ORDER BY p.public_id) FROM own_profiles p WHERE p.id IN(SELECT id FROM before_profiles UNION SELECT id FROM after_profiles) OR p.id=(SELECT profile_id FROM session)),'[]'::jsonb),
              'collections',COALESCE((SELECT jsonb_agg(to_jsonb(c) ORDER BY c.public_id) FROM own_collections c WHERE c.id IN(SELECT id FROM source_collections UNION SELECT id FROM result_collections)),'[]'::jsonb),
              'parents',COALESCE((SELECT jsonb_agg(to_jsonb(f) ORDER BY f.public_id) FROM cerberus.folder f CROSS JOIN actor a WHERE f.account_id=a.id AND f.id IN(SELECT parent_folder_id FROM visible UNION SELECT id FROM destination_visible)),'[]'::jsonb),
              'profileFolders',COALESCE((SELECT jsonb_agg(to_jsonb(pf) ORDER BY pf.profile_id,pf.folder_id) FROM cerberus.profile_folder pf WHERE pf.folder_id IN(SELECT id FROM members WHERE kind='folder')),'[]'::jsonb),
              'profileRecords',COALESCE((SELECT jsonb_agg(to_jsonb(pr) ORDER BY pr.profile_id,pr.record_id) FROM cerberus.profile_record pr WHERE pr.record_id IN(SELECT id FROM members WHERE kind='record')),'[]'::jsonb),
              'collectionFolders',COALESCE((SELECT jsonb_agg(to_jsonb(cf) ORDER BY cf.collection_id,cf.folder_id) FROM cerberus.collection_folder cf WHERE cf.folder_id IN(SELECT id FROM members WHERE kind='folder')),'[]'::jsonb),
              'collectionRecords',COALESCE((SELECT jsonb_agg(to_jsonb(cr) ORDER BY cr.collection_id,cr.record_id) FROM cerberus.collection_record cr WHERE cr.record_id IN(SELECT id FROM members WHERE kind='record')),'[]'::jsonb),
              'native',(SELECT evidence FROM native_result)) evidence
        )
        """;
}
