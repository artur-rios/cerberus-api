using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Trash;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
namespace ArturRios.Cerberus.Data.Records;

public sealed class RecordTrashStore(IDbContextFactory<AppDbContext> factory):IRecordTrashStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {PropertyNameCaseInsensitive=false,AllowDuplicateProperties=false,NumberHandling=JsonNumberHandling.Strict};

    public async Task<VaultResult<RecordTrashDetails>> TrashAsync(RecordTrashRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        if(request.RecordId==Guid.Empty || !Safe(request.ExpectedRevision))return new(Error:"validation_failed");
        try
        {
            await using var db=await factory.CreateDbContextAsync(ct);
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR NO KEY UPDATE",ct);
            var account=await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.HeimdallPublicId==request.Actor,ct);
            if(account is null || account.State!=AccountState.Active || await db.TerminalErasures.AnyAsync(x=>x.ResourceId==account.PublicId,ct))return new(Error:"not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE",ct);
            var initial=await SnapshotAsync(db,request,ct);
            var error=StateError(initial);if(error is not null)return new(Error:error);
            var item=JsonSerializer.Deserialize<Item>(initial.Item!,Json)!;
            // Foreign routes establish only whether this denial is visible. No foreign locks
            // or content inspection are necessary to deny both RO and RW delete requests.
            if(item.AccountId!=account.Id)return new(Error:ValidNative(initial)?"vault_access_denied":"persistence_unavailable");
            var original=await db.Records.AsNoTracking().SingleAsync(x=>x.Id==item.Id,ct);
            var originalFolder=original.FolderId;
            var profileIds=await db.ProfileRecords.Where(x=>x.RecordId==item.Id && x.AccountId==account.Id).Select(x=>x.ProfileId).ToArrayAsync(ct);
            var collectionIds=await db.CollectionRecords.Where(x=>x.RecordId==item.Id && x.AccountId==account.Id).Select(x=>x.CollectionId).ToArrayAsync(ct);
            var selection=await db.VaultAccessSessions.Where(x=>x.AccountId==account.Id && x.HandleVerifier==request.AccessVerifier).Select(x=>x.ProfileId).SingleAsync(ct);
            // All owned parents precede content. NO KEY UPDATE permits FK KEY SHARE checks;
            // the target UPDATE below excludes new references once admitted.
            var profiles=await db.Profiles.FromSqlInterpolated($"""
                SELECT p.* FROM cerberus.profile p WHERE p.account_id={account.Id}
                  AND (p.id=ANY({profileIds}) OR p.id={selection??0}) ORDER BY p.public_id FOR NO KEY UPDATE
                """).AsNoTracking().ToListAsync(ct);
            error=StateError(await SnapshotAsync(db,request,ct));if(error is not null)return new(Error:error);
            var collections=await db.Collections.FromSqlInterpolated($"""
                SELECT c.* FROM cerberus.collection c WHERE c.account_id={account.Id}
                  AND c.id=ANY({collectionIds}) ORDER BY c.public_id FOR NO KEY UPDATE
                """).AsNoTracking().ToListAsync(ct);
            error=StateError(await SnapshotAsync(db,request,ct));if(error is not null)return new(Error:error);
            VaultFolder? folder=null;
            if(originalFolder is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.folder WHERE account_id={account.Id} AND id={originalFolder} FOR NO KEY UPDATE",ct);
                folder=await db.Folders.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==originalFolder && x.AccountId==account.Id,ct);
                error=StateError(await SnapshotAsync(db,request,ct));if(error is not null)return new(Error:error);
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.record WHERE account_id={account.Id} AND id={item.Id} FOR UPDATE",ct);
            // Lock existing association rows after the target excludes phantom FK inserts.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile_record WHERE record_id={item.Id} ORDER BY profile_id FOR UPDATE",ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection_record WHERE record_id={item.Id} ORDER BY collection_id FOR UPDATE",ct);
            var current=await SnapshotAsync(db,request,ct);error=StateError(current);if(error is not null)return new(Error:error);
            original=await db.Records.AsNoTracking().SingleAsync(x=>x.Id==item.Id,ct);
            var actualProfiles=await db.ProfileRecords.Where(x=>x.RecordId==item.Id && x.AccountId==account.Id).Select(x=>x.ProfileId).ToArrayAsync(ct);
            var actualCollections=await db.CollectionRecords.Where(x=>x.RecordId==item.Id && x.AccountId==account.Id).Select(x=>x.CollectionId).ToArrayAsync(ct);
            var heldProfiles=profiles.Select(x=>x.Id).ToArray();var heldCollections=collections.Select(x=>x.Id).ToArray();
            if(original.FolderId!=originalFolder || actualProfiles.Except(heldProfiles).Any() || actualCollections.Except(heldCollections).Any())return new(Error:"revision_conflict");
            if(!Safe(original.Revision) || !Safe(original.ServerSequence) || original.EditedAt.Offset!=TimeSpan.Zero
                || original.EditedAt.Ticks<10 || original.EditedAt.Ticks%10!=0 || original.PurgeAt is not null)return new(Error:"persistence_unavailable");
            if(original.Revision!=request.ExpectedRevision || original.Revision==ProtocolBinary.MaxInteger
                || await db.TrashEntries.AnyAsync(x=>x.ResourceKind=="record" && x.ResourceId==request.RecordId,ct))return new(Error:"revision_conflict");
            var activeProfiles=new List<Profile>();var activeCollections=new List<VaultCollection>();
            foreach(var p in profiles.Where(x=>actualProfiles.Contains(x.Id)))
                if(p.DeletedAt is null && !await db.TerminalErasures.AnyAsync(x=>x.ResourceId==p.PublicId,ct))activeProfiles.Add(p);
            foreach(var c in collections.Where(x=>actualCollections.Contains(x.Id)))
                if(c.DeletedAt is null && !await db.TerminalErasures.AnyAsync(x=>x.ResourceId==c.PublicId,ct))activeCollections.Add(c);
            var parents=activeProfiles.Select(x=>new Parent("profile",x.Id,x.Revision,x.ServerSequence))
                .Concat(activeCollections.Select(x=>new Parent("collection",x.Id,x.Revision,x.ServerSequence))).ToList();
            if(folder is not null)parents.Add(new("folder",folder.Id,folder.Revision,folder.ServerSequence));
            if(parents.Any(x=>!Safe(x.Revision) || !Safe(x.Sequence)))return new(Error:"persistence_unavailable");
            if(parents.Any(x=>x.Revision==ProtocolBinary.MaxInteger))return new(Error:"revision_conflict");
            var snapshot=new RecordAssociationSnapshot(folder?.PublicId,
                profiles.Where(x=>actualProfiles.Contains(x.Id)).Select(x=>x.PublicId).Order().ToArray(),
                collections.Where(x=>actualCollections.Contains(x.Id)).Select(x=>x.PublicId).Order().ToArray());
            var parameters=Parameters(request).Concat(new object[]{new NpgsqlParameter("expected",request.ExpectedRevision),
                new NpgsqlParameter("previous_sequence",original.ServerSequence),new NpgsqlParameter("original",original.Envelope),
                new NpgsqlParameter("parent",NpgsqlDbType.Bigint){Value=(object?)originalFolder??DBNull.Value},
                new NpgsqlParameter("profiles",heldProfiles),new NpgsqlParameter("collections",heldCollections),new NpgsqlParameter("stamp",Guid.NewGuid())}).ToArray();
            var changed=await db.Database.ExecuteSqlRawAsync(Authority+"""
                UPDATE cerberus.record AS r SET deleted_at=statement_timestamp(),purge_at=statement_timestamp()+interval '720 hours',
                  folder_id=NULL,revision=r.revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp
                FROM visible v CROSS JOIN session s WHERE r.id=v.id AND r.account_id=s.account_id AND r.public_id=@target
                  AND r.revision=@expected AND v.revision=@expected AND v.server_sequence=@previous_sequence AND v.envelope=@original
                  AND v.folder_id IS NOT DISTINCT FROM @parent AND r.purge_at IS NULL
                  AND NOT EXISTS(SELECT FROM cerberus.profile_record pr WHERE pr.record_id=r.id AND NOT(pr.profile_id=ANY(@profiles)))
                  AND NOT EXISTS(SELECT FROM cerberus.collection_record cr WHERE cr.record_id=r.id AND NOT(cr.collection_id=ANY(@collections)))
                """,parameters,ct);
            if(changed!=1)return new(Error:StateError(await SnapshotAsync(db,request,ct))??"revision_conflict");
            var row=await db.Records.AsNoTracking().SingleAsync(x=>x.Id==original.Id,ct);
            if(row.Revision!=original.Revision+1 || !Safe(row.ServerSequence) || row.ServerSequence<=original.ServerSequence
                || row.DeletedAt is null || row.DeletedAt.Value.Ticks<10 || row.DeletedAt.Value.Offset!=TimeSpan.Zero || row.DeletedAt.Value.Ticks%10!=0
                || row.PurgeAt is null || row.PurgeAt.Value.Offset!=TimeSpan.Zero || row.PurgeAt-row.DeletedAt!=TimeSpan.FromDays(30)
                || row.FolderId is not null || row.AccountId!=original.AccountId || row.EditedAt!=original.EditedAt
                || !row.Envelope.AsSpan().SequenceEqual(original.Envelope))return new(Error:"persistence_unavailable");
            await db.ProfileRecords.Where(x=>x.RecordId==original.Id).ExecuteDeleteAsync(ct);
            await db.CollectionRecords.Where(x=>x.RecordId==original.Id).ExecuteDeleteAsync(ct);
            foreach(var parent in parents)
            {
                error=await BumpAsync(db,account.Id,parent,ct);if(error is not null)return new(Error:error);
            }
            var operation=new TrashOperation{AccountId=account.Id,RootResourceKind="record",RootResourceId=row.PublicId,
                DeletedAt=row.DeletedAt.Value,PurgeAt=row.PurgeAt.Value};
            db.TrashOperations.Add(operation);await db.SaveChangesAsync(ct);
            db.TrashEntries.Add(new(){OperationId=operation.Id,ResourceKind="record",ResourceId=row.PublicId,AssociationSnapshot=JsonSerializer.SerializeToUtf8Bytes(snapshot,Json)});
            db.RetentionWorkItems.Add(new RetentionWorkItem{OperationKey="trash/"+operation.PublicId,DueAt=operation.PurgeAt});
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(new(row.PublicId,operation.PublicId,row.Revision,row.ServerSequence,operation.DeletedAt,operation.PurgeAt));
        }
        catch(PostgresException e) when(e.SqlState=="2200H"){return new(Error:"revision_conflict");}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return new(Error:"persistence_unavailable");}
        catch(Exception e) when(e is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            || e is InvalidOperationException {InnerException:DbException or DbUpdateException or TimeoutException}){return new(Error:"persistence_unavailable");}
    }
    private sealed record Parent(string Kind,long Id,long Revision,long Sequence);
    private sealed class ParentMetadata{public long Revision{get;set;}public long ServerSequence{get;set;}}
    private static async Task<string?> BumpAsync(AppDbContext db,long account,Parent parent,CancellationToken ct)
    {
        var update=parent.Kind switch{
            "profile"=>"UPDATE cerberus.profile SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp WHERE id=@id AND account_id=@owner AND revision=@revision AND server_sequence=@sequence AND deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=cerberus.profile.public_id)",
            "collection"=>"UPDATE cerberus.collection SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp WHERE id=@id AND account_id=@owner AND revision=@revision AND server_sequence=@sequence AND deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=cerberus.collection.public_id)",
            _=>"UPDATE cerberus.folder SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp WHERE id=@id AND account_id=@owner AND revision=@revision AND server_sequence=@sequence AND deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=cerberus.folder.public_id)"};
        object[] args=[new NpgsqlParameter("stamp",Guid.NewGuid()),new NpgsqlParameter("id",parent.Id),new NpgsqlParameter("owner",account),new NpgsqlParameter("revision",parent.Revision),new NpgsqlParameter("sequence",parent.Sequence)];
        if(await db.Database.ExecuteSqlRawAsync(update,args,ct)!=1)return "revision_conflict";
        var select=parent.Kind switch{"profile"=>"SELECT revision,server_sequence FROM cerberus.profile WHERE id=@id","collection"=>"SELECT revision,server_sequence FROM cerberus.collection WHERE id=@id",_=>"SELECT revision,server_sequence FROM cerberus.folder WHERE id=@id"};
        var current=(await db.Database.SqlQueryRaw<ParentMetadata>(select,new NpgsqlParameter("id",parent.Id)).ToListAsync(ct)).Single();
        return current.Revision!=parent.Revision+1 || !Safe(current.ServerSequence) || current.ServerSequence<=parent.Sequence?"persistence_unavailable":null;
    }
    private static bool Safe(long n)=>n is >0 and <=ProtocolBinary.MaxInteger;
    private static string? StateError(Snapshot s)=>!s.ActorActive?"not_found":!s.Allowed?"vault_access_denied":s.CorruptAncestry?"persistence_unavailable":s.Item is null?"not_found":null;
    private static bool ValidNative(Snapshot snapshot)
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
    private static object[] Parameters(RecordTrashRequest r)=>[new NpgsqlParameter("actor",r.Actor),new NpgsqlParameter("verifier",r.AccessVerifier),new NpgsqlParameter("target",r.RecordId),new NpgsqlParameter("active",(object)(int)AccountState.Active),new NpgsqlParameter("grant_active",(object)(int)CollectionGrantState.Active),new NpgsqlParameter("read_only",(object)(int)CollectionGrantAccess.ReadOnly),new NpgsqlParameter("read_write",(object)(int)CollectionGrantAccess.ReadWrite),new NpgsqlParameter("max",ProtocolBinary.MaxInteger)];
    private static async Task<Snapshot> SnapshotAsync(AppDbContext db,RecordTrashRequest request,CancellationToken ct)
    {
        var rows=await db.Database.SqlQueryRaw<Snapshot>(Authority+SnapshotSelect,Parameters(request)).ToListAsync(ct);
        return rows.Single();
    }
    private sealed record Item(Guid RecordId,long Id,long AccountId,long Revision,long Sequence,DateTimeOffset EditedAt,string Envelope);
    private sealed class Snapshot
    {
        public bool ActorActive{get;set;}public bool Allowed{get;set;}public bool CorruptAncestry{get;set;}public bool Writable{get;set;}
        public string? Item{get;set;}public long[] Collections{get;set;}=[];public long[] GrantsIds{get;set;}=[];
        public string Grants{get;set;}="[]";public string? Protection{get;set;}
    }
    private sealed record GrantEvidence(Guid CollectionId,long CollectionEpoch,string CollectionEnvelope,Guid GrantId,long GrantRevision,string GrantEnvelope);
    private sealed record ProtectionEvidence(Guid OwnerId,Guid RecipientIdentity,string? OwnerMaterial,long? OwnerRevision,long? OwnerEpoch,long? OwnerGeneration,string? RecipientMaterial,long? RecipientRevision,long? RecipientEpoch,long? RecipientGeneration);
    private const string SnapshotSelect="""
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
    private const string Authority="""
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
}
