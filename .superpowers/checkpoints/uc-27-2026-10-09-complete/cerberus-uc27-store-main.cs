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
