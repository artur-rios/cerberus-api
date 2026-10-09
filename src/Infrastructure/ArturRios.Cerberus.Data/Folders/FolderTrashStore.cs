using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Trash;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
namespace ArturRios.Cerberus.Data.Folders;

public sealed class FolderTrashStore(IDbContextFactory<AppDbContext> factory):IFolderTrashStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {PropertyNameCaseInsensitive=false,AllowDuplicateProperties=false,NumberHandling=JsonNumberHandling.Strict};
    public async Task<VaultResult<FolderTrashDetails>> TrashAsync(FolderTrashRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        if(request.FolderId==Guid.Empty||!Safe(request.ExpectedRevision))return new(Error:"validation_failed");
        try
        {
            await using var db=await factory.CreateDbContextAsync(ct);
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            // Own account serializes every current same-owner topology writer, including
            // implicit account FK checks. Cross-owner editors never lock this account.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR UPDATE",ct);
            var account=await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.HeimdallPublicId==request.Actor,ct);
            if(account is null||account.State!=AccountState.Active||await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==account.PublicId,ct))return new(Error:"not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE",ct);
            var initial=await SnapshotAsync(db,request,ct);var error=StateError(initial);if(error is not null)return new(Error:error);
            var target=JsonSerializer.Deserialize<Item>(initial.Item!,Json)!;
            if(target.AccountId!=account.Id)return new(Error:ValidNative(initial)?"vault_access_denied":"persistence_unavailable");
            var captured=await InventoryAsync(db,request,ct);var inventory=JsonSerializer.Deserialize<Inventory>(captured,Json)!;
            if(inventory.Cycle)return new(Error:"persistence_unavailable");
            var folderIds=inventory.Folders.Select(x=>x.Id).ToArray();var recordIds=inventory.Records.Select(x=>x.Id).ToArray();
            var profileIds=inventory.Profiles.Select(x=>x.Id).ToArray();var collectionIds=inventory.Collections.Select(x=>x.Id).ToArray();
            var structuralFolders=folderIds.Concat(inventory.Parent is null?[]:new[]{inventory.Parent.Id}).Distinct().ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE account_id={account.Id} AND id=ANY({profileIds}) ORDER BY public_id FOR NO KEY UPDATE",ct);
            error=StateError(await SnapshotAsync(db,request,ct));if(error is not null)return new(Error:error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE account_id={account.Id} AND id=ANY({collectionIds}) ORDER BY public_id FOR NO KEY UPDATE",ct);
            error=StateError(await SnapshotAsync(db,request,ct));if(error is not null)return new(Error:error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.folder WHERE account_id={account.Id} AND id=ANY({structuralFolders}) ORDER BY public_id FOR UPDATE",ct);
            error=StateError(await SnapshotAsync(db,request,ct));if(error is not null)return new(Error:error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.record WHERE account_id={account.Id} AND id=ANY({recordIds}) ORDER BY public_id FOR UPDATE",ct);
            error=StateError(await SnapshotAsync(db,request,ct));if(error is not null)return new(Error:error);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile_folder WHERE folder_id=ANY({folderIds}) ORDER BY profile_id,folder_id FOR UPDATE",ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection_folder WHERE folder_id=ANY({folderIds}) ORDER BY collection_id,folder_id FOR UPDATE",ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile_record WHERE record_id=ANY({recordIds}) ORDER BY profile_id,record_id FOR UPDATE",ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection_record WHERE record_id=ANY({recordIds}) ORDER BY collection_id,record_id FOR UPDATE",ct);
            error=StateError(await SnapshotAsync(db,request,ct));if(error is not null)return new(Error:error);
            if(captured!=await InventoryAsync(db,request,ct))return new(Error:"revision_conflict");
            var root=inventory.Folders.Single(x=>x.PublicId==request.FolderId);
            var linkedProfiles=inventory.ProfileFolders.Select(x=>x.ProfileId).Concat(inventory.ProfileRecords.Select(x=>x.ProfileId)).ToHashSet();
            var parents=inventory.Profiles.Where(x=>linkedProfiles.Contains(x.Id)&&x.DeletedAt is null&&!x.Terminal).Select(x=>new Parent("profile",x))
                .Concat(inventory.Collections.Where(x=>x.DeletedAt is null&&!x.Terminal).Select(x=>new Parent("collection",x)))
                .Concat(inventory.Parent is null?[]:new[]{new Parent("folder",inventory.Parent)}).ToArray();
            if(inventory.Folders.Concat(inventory.Records).Any(x=>!Safe(x.Revision)||!Safe(x.Sequence)||!Time(x.EditedAt)||x.PurgeAt is not null)
                ||parents.Any(x=>!Safe(x.Row.Revision)||!Safe(x.Row.Sequence)||!Time(x.Row.EditedAt)))return new(Error:"persistence_unavailable");
            if(root.Revision!=request.ExpectedRevision||inventory.Folders.Concat(inventory.Records).Any(x=>x.Revision==ProtocolBinary.MaxInteger)
                ||parents.Any(x=>x.Row.Revision==ProtocolBinary.MaxInteger))return new(Error:"revision_conflict");
            var folderGuids=inventory.Folders.Select(x=>x.PublicId).ToArray();var recordGuids=inventory.Records.Select(x=>x.PublicId).ToArray();
            if(await db.TrashEntries.AnyAsync(x=>x.ResourceKind=="folder"&&folderGuids.Contains(x.ResourceId)||x.ResourceKind=="record"&&recordGuids.Contains(x.ResourceId),ct))return new(Error:"revision_conflict");
            var args=Parameters(request).Concat(RowParameters(root)).Concat(new object[]{new NpgsqlParameter("inventory",NpgsqlDbType.Jsonb){Value=captured}}).ToArray();
            var changed=await db.Database.ExecuteSqlRawAsync(Authority+InventoryCtes+", payload AS ("+InventorySelect+") "+"""
                UPDATE cerberus.folder AS root SET deleted_at=statement_timestamp(),purge_at=statement_timestamp()+interval '720 hours',
                    parent_folder_id=NULL,revision=root.revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp
                FROM visible v CROSS JOIN session s CROSS JOIN payload p WHERE root.id=v.id AND root.account_id=s.account_id
                    AND root.id=@id AND root.revision=@revision AND root.server_sequence=@sequence AND root.envelope=@envelope
                    AND root.parent_folder_id IS NOT DISTINCT FROM @parent AND root.purge_at IS NULL AND p.value::jsonb=@inventory
                """,args,ct);
            if(changed!=1)return new(Error:StateError(await SnapshotAsync(db,request,ct))??"revision_conflict");
            var result=await db.Folders.AsNoTracking().SingleAsync(x=>x.Id==root.Id,ct);
            if(!Changed(root,result.PublicId,result.AccountId,result.Revision,result.ServerSequence,result.Envelope,result.EditedAt,result.DeletedAt,result.PurgeAt,result.ParentFolderId))return new(Error:"persistence_unavailable");
            var deleted=result.DeletedAt!.Value;var purge=result.PurgeAt!.Value;
            foreach(var row in inventory.Folders.Where(x=>x.Id!=root.Id))
            {
                error=await TrashMemberAsync(db,"folder",row,deleted,purge,ct);if(error is not null)return new(Error:error);
            }
            foreach(var row in inventory.Records)
            {
                error=await TrashMemberAsync(db,"record",row,deleted,purge,ct);if(error is not null)return new(Error:error);
            }
            await db.ProfileFolders.Where(x=>folderIds.Contains(x.FolderId)).ExecuteDeleteAsync(ct);
            await db.CollectionFolders.Where(x=>folderIds.Contains(x.FolderId)).ExecuteDeleteAsync(ct);
            await db.ProfileRecords.Where(x=>recordIds.Contains(x.RecordId)).ExecuteDeleteAsync(ct);
            await db.CollectionRecords.Where(x=>recordIds.Contains(x.RecordId)).ExecuteDeleteAsync(ct);
            foreach(var parent in parents){error=await BumpAsync(db,account.Id,parent,ct);if(error is not null)return new(Error:error);}
            var operation=new TrashOperation{AccountId=account.Id,RootResourceKind="folder",RootResourceId=root.PublicId,DeletedAt=deleted,PurgeAt=purge};
            db.TrashOperations.Add(operation);await db.SaveChangesAsync(ct);
            Guid? FolderGuid(long? id)=>id is null?null:inventory.Folders.SingleOrDefault(x=>x.Id==id)?.PublicId??(inventory.Parent?.Id==id?inventory.Parent.PublicId:null);
            Guid[] Profiles(IEnumerable<long> ids)=>ids.Select(id=>inventory.Profiles.Single(x=>x.Id==id).PublicId).Order().ToArray();
            Guid[] Collections(IEnumerable<long> ids)=>ids.Select(id=>inventory.Collections.Single(x=>x.Id==id).PublicId).Order().ToArray();
            foreach(var row in inventory.Folders)db.TrashEntries.Add(new(){OperationId=operation.Id,ResourceKind="folder",ResourceId=row.PublicId,
                AssociationSnapshot=JsonSerializer.SerializeToUtf8Bytes(new FolderAssociationSnapshot(FolderGuid(row.Parent),Profiles(inventory.ProfileFolders.Where(x=>x.FolderId==row.Id).Select(x=>x.ProfileId)),Collections(inventory.CollectionFolders.Where(x=>x.FolderId==row.Id).Select(x=>x.CollectionId))),Json)});
            foreach(var row in inventory.Records)db.TrashEntries.Add(new(){OperationId=operation.Id,ResourceKind="record",ResourceId=row.PublicId,
                AssociationSnapshot=JsonSerializer.SerializeToUtf8Bytes(new RecordAssociationSnapshot(FolderGuid(row.Parent),Profiles(inventory.ProfileRecords.Where(x=>x.RecordId==row.Id).Select(x=>x.ProfileId)),Collections(inventory.CollectionRecords.Where(x=>x.RecordId==row.Id).Select(x=>x.CollectionId))),Json)});
            await db.SaveChangesAsync(ct);
            db.RetentionWorkItems.Add(new RetentionWorkItem{OperationKey="trash/"+operation.PublicId,DueAt=purge});await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(new(root.PublicId,operation.PublicId,result.Revision,result.ServerSequence,deleted,purge));
        }
        catch(PostgresException e) when(e.SqlState=="2200H"){return new(Error:"revision_conflict");}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return new(Error:"persistence_unavailable");}
        catch(Exception e) when(e is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            ||e is InvalidOperationException{InnerException:DbException or DbUpdateException or TimeoutException}){return new(Error:"persistence_unavailable");}
    }
    private static bool Time(DateTimeOffset value)=>value.Offset==TimeSpan.Zero&&value.Ticks>=10&&value.Ticks%10==0;
    private static bool Changed(Resource old,Guid id,long account,long revision,long sequence,byte[] envelope,DateTimeOffset edited,DateTimeOffset? deleted,DateTimeOffset? purge,long? parent)
        =>id==old.PublicId&&account==old.AccountId&&revision==old.Revision+1&&Safe(sequence)&&sequence>old.Sequence
          &&envelope.AsSpan().SequenceEqual(Convert.FromHexString(old.Envelope))&&edited==old.EditedAt&&parent is null
          &&deleted is not null&&Time(deleted.Value)&&purge is not null&&Time(purge.Value)&&purge-deleted==TimeSpan.FromDays(30);
    private static object[] RowParameters(Resource row)=>[new NpgsqlParameter("id",row.Id),new NpgsqlParameter("owner",row.AccountId),new NpgsqlParameter("revision",row.Revision),new NpgsqlParameter("sequence",row.Sequence),new NpgsqlParameter("envelope",Convert.FromHexString(row.Envelope)),new NpgsqlParameter("parent",NpgsqlDbType.Bigint){Value=(object?)row.Parent??DBNull.Value},new NpgsqlParameter("stamp",Guid.NewGuid())];
    private static async Task<string?> TrashMemberAsync(AppDbContext db,string kind,Resource row,DateTimeOffset deleted,DateTimeOffset purge,CancellationToken ct)
    {
        var parent=kind=="folder"?"parent_folder_id":"folder_id";
        var sql=$"UPDATE cerberus.{kind} AS child SET deleted_at=@deleted,purge_at=@purge,{parent}=NULL,revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp WHERE id=@id AND account_id=@owner AND revision=@revision AND server_sequence=@sequence AND envelope=@envelope AND {parent} IS NOT DISTINCT FROM @parent AND deleted_at IS NULL AND purge_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='{kind}' AND e.resource_id=child.public_id)";
        if(await db.Database.ExecuteSqlRawAsync(sql,RowParameters(row).Concat(new object[]{new NpgsqlParameter("deleted",deleted),new NpgsqlParameter("purge",purge)}).ToArray(),ct)!=1)return "revision_conflict";
        if(kind=="folder")
        {var r=await db.Folders.AsNoTracking().SingleAsync(x=>x.Id==row.Id,ct);return Changed(row,r.PublicId,r.AccountId,r.Revision,r.ServerSequence,r.Envelope,r.EditedAt,r.DeletedAt,r.PurgeAt,r.ParentFolderId)&&r.DeletedAt==deleted&&r.PurgeAt==purge?null:"persistence_unavailable";}
        else
        {var r=await db.Records.AsNoTracking().SingleAsync(x=>x.Id==row.Id,ct);return Changed(row,r.PublicId,r.AccountId,r.Revision,r.ServerSequence,r.Envelope,r.EditedAt,r.DeletedAt,r.PurgeAt,r.FolderId)&&r.DeletedAt==deleted&&r.PurgeAt==purge?null:"persistence_unavailable";}
    }
    private sealed record Resource(Guid PublicId,long Id,long AccountId,long Revision,long Sequence,DateTimeOffset EditedAt,string Envelope,long? Parent,DateTimeOffset? PurgeAt);
    private sealed record External(long Id,Guid PublicId,long Revision,long Sequence,DateTimeOffset EditedAt,DateTimeOffset? DeletedAt,bool Terminal);
    private sealed record Parent(string Kind,External Row);
    private sealed record PF(long ProfileId,long FolderId);
    private sealed record PR(long ProfileId,long RecordId);
    private sealed record CF(long CollectionId,long FolderId);
    private sealed record CR(long CollectionId,long RecordId);
    private sealed record Inventory(Resource[] Folders,Resource[] Records,External[] Profiles,External[] Collections,External? Parent,PF[] ProfileFolders,PR[] ProfileRecords,CF[] CollectionFolders,CR[] CollectionRecords,bool Cycle);
    private sealed class InventoryRow{public string Value{get;set;}="";}
    private static async Task<string> InventoryAsync(AppDbContext db,FolderTrashRequest request,CancellationToken ct)
        =>(await db.Database.SqlQueryRaw<InventoryRow>(Authority+InventoryCtes+InventorySelect,Parameters(request)).ToListAsync(ct)).Single().Value;
    private sealed class ParentMetadata{public long Revision{get;set;}public long ServerSequence{get;set;}}
    private static async Task<string?> BumpAsync(AppDbContext db,long account,Parent parent,CancellationToken ct)
    {
        var update=parent.Kind switch{
            "profile"=>"UPDATE cerberus.profile SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp WHERE id=@id AND account_id=@owner AND revision=@revision AND server_sequence=@sequence AND deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='profile' AND e.resource_id=cerberus.profile.public_id))",
            "collection"=>"UPDATE cerberus.collection SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp WHERE id=@id AND account_id=@owner AND revision=@revision AND server_sequence=@sequence AND deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='collection' AND e.resource_id=cerberus.collection.public_id))",
            _=>"UPDATE cerberus.folder SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp WHERE id=@id AND account_id=@owner AND revision=@revision AND server_sequence=@sequence AND deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='folder' AND e.resource_id=cerberus.folder.public_id))"};
        object[] args=[new NpgsqlParameter("stamp",Guid.NewGuid()),new NpgsqlParameter("id",parent.Row.Id),new NpgsqlParameter("owner",account),new NpgsqlParameter("revision",parent.Row.Revision),new NpgsqlParameter("sequence",parent.Row.Sequence)];
        if(await db.Database.ExecuteSqlRawAsync(update,args,ct)!=1)return "revision_conflict";
        var select=parent.Kind switch{"profile"=>"SELECT revision,server_sequence FROM cerberus.profile WHERE id=@id","collection"=>"SELECT revision,server_sequence FROM cerberus.collection WHERE id=@id",_=>"SELECT revision,server_sequence FROM cerberus.folder WHERE id=@id"};
        var current=(await db.Database.SqlQueryRaw<ParentMetadata>(select,new NpgsqlParameter("id",parent.Row.Id)).ToListAsync(ct)).Single();
        return current.Revision!=parent.Row.Revision+1 || !Safe(current.ServerSequence) || current.ServerSequence<=parent.Row.Sequence?"persistence_unavailable":null;
    }
    private const string InventoryCtes="""
        , tree AS (
            SELECT f.*,ARRAY[f.id] path,false cycle FROM visible f CROSS JOIN session s WHERE f.account_id=s.account_id
            UNION ALL
            SELECT f.*,t.path||f.id,f.id=ANY(t.path) FROM tree t JOIN cerberus.folder f
                ON f.parent_folder_id=t.id AND f.account_id=t.account_id
            WHERE NOT t.cycle AND f.deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
        ), members AS MATERIALIZED (
            SELECT r.* FROM cerberus.record r JOIN tree t ON r.folder_id=t.id AND r.account_id=t.account_id
            WHERE r.deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='record' AND e.resource_id=r.public_id)
        )
        """;
    private const string InventorySelect="""
        SELECT jsonb_build_object(
            'folders',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',f.id,'publicId',f.public_id,'accountId',f.account_id,'revision',f.revision,'sequence',f.server_sequence,'editedAt',f.edited_at,'envelope',encode(f.envelope,'hex'),'parent',f.parent_folder_id,'purgeAt',f.purge_at) ORDER BY f.public_id) FROM tree f),'[]'::jsonb),
            'records',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',r.id,'publicId',r.public_id,'accountId',r.account_id,'revision',r.revision,'sequence',r.server_sequence,'editedAt',r.edited_at,'envelope',encode(r.envelope,'hex'),'parent',r.folder_id,'purgeAt',r.purge_at) ORDER BY r.public_id) FROM members r),'[]'::jsonb),
            'profiles',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',p.id,'publicId',p.public_id,'revision',p.revision,'sequence',p.server_sequence,'editedAt',p.edited_at,'deletedAt',p.deleted_at,'terminal',EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id)) ORDER BY p.public_id) FROM cerberus.profile p CROSS JOIN session s WHERE p.account_id=s.account_id AND (p.id=s.profile_id OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN tree t ON t.id=pf.folder_id WHERE pf.profile_id=p.id) OR EXISTS(SELECT FROM cerberus.profile_record pr JOIN members r ON r.id=pr.record_id WHERE pr.profile_id=p.id))),'[]'::jsonb),
            'collections',COALESCE((SELECT jsonb_agg(jsonb_build_object('id',c.id,'publicId',c.public_id,'revision',c.revision,'sequence',c.server_sequence,'editedAt',c.edited_at,'deletedAt',c.deleted_at,'terminal',EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='collection' AND e.resource_id=c.public_id)) ORDER BY c.public_id) FROM cerberus.collection c CROSS JOIN session s WHERE c.account_id=s.account_id AND (EXISTS(SELECT FROM cerberus.collection_folder cf JOIN tree t ON t.id=cf.folder_id WHERE cf.collection_id=c.id) OR EXISTS(SELECT FROM cerberus.collection_record cr JOIN members r ON r.id=cr.record_id WHERE cr.collection_id=c.id))),'[]'::jsonb),
            'parent',(SELECT jsonb_build_object('id',f.id,'publicId',f.public_id,'revision',f.revision,'sequence',f.server_sequence,'editedAt',f.edited_at,'deletedAt',f.deleted_at,'terminal',EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)) FROM visible v JOIN cerberus.folder f ON f.id=v.parent_folder_id AND f.account_id=v.account_id),
            'profileFolders',COALESCE((SELECT jsonb_agg(jsonb_build_object('profileId',pf.profile_id,'folderId',pf.folder_id) ORDER BY pf.profile_id,pf.folder_id) FROM cerberus.profile_folder pf JOIN tree t ON t.id=pf.folder_id),'[]'::jsonb),
            'profileRecords',COALESCE((SELECT jsonb_agg(jsonb_build_object('profileId',pr.profile_id,'recordId',pr.record_id) ORDER BY pr.profile_id,pr.record_id) FROM cerberus.profile_record pr JOIN members t ON t.id=pr.record_id),'[]'::jsonb),
            'collectionFolders',COALESCE((SELECT jsonb_agg(jsonb_build_object('collectionId',cf.collection_id,'folderId',cf.folder_id) ORDER BY cf.collection_id,cf.folder_id) FROM cerberus.collection_folder cf JOIN tree t ON t.id=cf.folder_id),'[]'::jsonb),
            'collectionRecords',COALESCE((SELECT jsonb_agg(jsonb_build_object('collectionId',cr.collection_id,'recordId',cr.record_id) ORDER BY cr.collection_id,cr.record_id) FROM cerberus.collection_record cr JOIN members t ON t.id=cr.record_id),'[]'::jsonb),
            'cycle',EXISTS(SELECT FROM tree WHERE cycle))::text AS value
        """;
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
    private static object[] Parameters(FolderTrashRequest r)=>[new NpgsqlParameter("actor",r.Actor),new NpgsqlParameter("verifier",r.AccessVerifier),new NpgsqlParameter("target",r.FolderId),new NpgsqlParameter("active",(object)(int)AccountState.Active),new NpgsqlParameter("grant_active",(object)(int)CollectionGrantState.Active),new NpgsqlParameter("read_only",(object)(int)CollectionGrantAccess.ReadOnly),new NpgsqlParameter("read_write",(object)(int)CollectionGrantAccess.ReadWrite),new NpgsqlParameter("max",ProtocolBinary.MaxInteger)];
    private static async Task<Snapshot> SnapshotAsync(AppDbContext db,FolderTrashRequest request,CancellationToken ct)
    {
        var rows=await db.Database.SqlQueryRaw<Snapshot>(Authority+SnapshotSelect,Parameters(request)).ToListAsync(ct);
        return rows.Single();
    }
    private sealed record Item(Guid FolderId,long Id,long AccountId,long Revision,long Sequence,DateTimeOffset EditedAt,string Envelope);
    private sealed class Snapshot
    {
        public bool ActorActive{get;set;}public bool Allowed{get;set;}public bool CorruptAncestry{get;set;}public bool Writable{get;set;}
        public string? Item{get;set;}public long[] Collections{get;set;}=[];public long[] GrantsIds{get;set;}=[];
        public string Grants{get;set;}="[]";public string? Protection{get;set;}
    }
    private sealed record GrantEvidence(Guid CollectionId,long CollectionEpoch,string CollectionEnvelope,Guid GrantId,long GrantRevision,string GrantEnvelope);
    private sealed record ProtectionEvidence(Guid OwnerId,Guid RecipientIdentity,string? OwnerMaterial,long? OwnerRevision,long? OwnerEpoch,long? OwnerGeneration,string? RecipientMaterial,long? RecipientRevision,long? RecipientEpoch,long? RecipientGeneration);
    private const string SnapshotSelect="""
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
    private const string Authority="""
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
}
