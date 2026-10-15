using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Trash;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace ArturRios.Cerberus.Data.Trash;

public sealed class TrashRestoreStore(IDbContextFactory<AppDbContext> factory):ITrashRestoreStore
{
    public async Task<VaultResult<TrashRestoreDetails>> RestoreAsync(TrashRestoreRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        if(request.OperationId==Guid.Empty||!TrashRestoreInventory.Safe(request.ExpectedRevision))return new(Error:"validation_failed");
        try
        {
            await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR UPDATE",ct);
            var account=await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.HeimdallPublicId==request.Actor,ct);
            if(account is null||account.State!=AccountState.Active||await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==account.PublicId,ct))return new(Error:"not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE",ct);
            var authority=await AuthorityAsync(db,request,ct);var error=AuthorityError(authority);if(error is not null)return new(Error:error);
            var operation=await db.TrashOperations.AsNoTracking().SingleOrDefaultAsync(x=>x.PublicId==request.OperationId&&x.AccountId==account.Id,ct);
            if(operation is null)return new(Error:"not_found");
            if(!TrashRestoreInventory.Kind(operation.RootResourceKind))return new(Error:authority.ProfileId is null?"persistence_unavailable":"not_found");
            var entry=await db.TrashEntries.AsNoTracking().SingleOrDefaultAsync(x=>x.OperationId==operation.Id&&x.ResourceKind==operation.RootResourceKind&&x.ResourceId==operation.RootResourceId,ct);
            var root=await TrashRestoreInventory.ReadAsync(db,operation.RootResourceKind,account.Id,operation.RootResourceId,ct);
            if(entry is null||root is null||root.DeletedAt is null||await Terminal(root))return new(Error:"not_found");
            if(!await Scope(root,entry,authority))return new(Error:"not_found");
            error=RootError(root,operation,request,authority.Now);if(error is not null)return new(Error:error);
            // Claiming is a separate short transaction. Both restore and expiry acquire
            // the owner first, then this job; deletion invalidates a previously held claim.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.retention_work_item WHERE operation_key={"trash/"+operation.PublicId} FOR UPDATE",ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.trash_operation WHERE id={operation.Id} AND account_id={account.Id} FOR UPDATE",ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.trash_entry WHERE operation_id={operation.Id} ORDER BY resource_kind,resource_id FOR UPDATE",ct);
            var entries=await db.TrashEntries.AsNoTracking().Where(x=>x.OperationId==operation.Id).OrderBy(x=>x.ResourceKind).ThenBy(x=>x.ResourceId).ToListAsync(ct);
            if(operation.RootResourceKind!="folder"?entries.Count!=1:entries.Any(x=>x.ResourceKind is not ("folder" or "record")))return new(Error:"persistence_unavailable");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE account_id={account.Id} ORDER BY public_id FOR NO KEY UPDATE",ct);
            if(root.Kind!="profile")await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE account_id={account.Id} ORDER BY public_id FOR NO KEY UPDATE",ct);
            var resources=new List<RestoringTrashResource>();var missing=0;
            foreach(var item in entries.OrderBy(x=>x.ResourceKind==root.Kind&&x.ResourceId==root.PublicId?0:1).ThenBy(x=>x.ResourceKind).ThenBy(x=>x.ResourceId))
            {
                var row=await TrashRestoreInventory.ReadAsync(db,item.ResourceKind,account.Id,item.ResourceId,ct);
                if(row is null||await Terminal(row)){if(item.ResourceKind==root.Kind&&item.ResourceId==root.PublicId)return new(Error:"not_found");missing++;continue;}
                if(row.DeletedAt is null)return new(Error:"revision_conflict");
                if(!TrashAssociations.TryRead(row.Kind,item.AssociationSnapshot,out var snapshot))return new(Error:"persistence_unavailable");
                resources.Add(new(row,item,snapshot!));
            }
            if(resources.Count==0)return new(Error:"not_found");
            var wantedFolders=resources.SelectMany(x=>x.Associations.FolderIds.Concat(x.Associations.Parent is {} parent?[parent]:[])
                .Concat(x.Row.Kind=="folder"?[x.Row.PublicId]:[])).Distinct().ToArray();
            var wantedRecords=resources.SelectMany(x=>x.Associations.RecordIds.Concat(x.Row.Kind=="record"?[x.Row.PublicId]:[])).Distinct().ToArray();
            if(root.Kind=="profile")
            {
                var wantedCollections=resources[0].Associations.CollectionIds;
                // SHARE-only collection acquisition avoids reciprocal profile restores
                // holding strong own collection locks before asking for foreign ones.
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE public_id=ANY({wantedCollections}) ORDER BY public_id FOR SHARE",ct);
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.folder WHERE account_id={account.Id} AND public_id=ANY({wantedFolders}) ORDER BY public_id FOR UPDATE",ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.record WHERE account_id={account.Id} AND public_id=ANY({wantedRecords}) ORDER BY public_id FOR UPDATE",ct);
            authority=await AuthorityAsync(db,request,ct);error=AuthorityError(authority);if(error is not null)return new(Error:error);
            // Reload every retained row after resource waits. Only currently retained
            // members of this operation can enter the mutation inventory.
            var locked=new List<RestoringTrashResource>();
            foreach(var resource in resources)
            {
                var row=await TrashRestoreInventory.ReadAsync(db,resource.Row.Kind,account.Id,resource.Row.PublicId,ct);
                if(row is null||await Terminal(resource.Row)){if(resource.Row.PublicId==root.PublicId&&resource.Row.Kind==root.Kind)return new(Error:"not_found");missing++;continue;}
                if(row.DeletedAt is null)return new(Error:"revision_conflict");
                if(!ValidRetained(row,operation)||row.Parent is not null||!await TrashRestoreInventory.NativeAsync(db,row,account.PublicId,request.Actor,ct))return new(Error:"persistence_unavailable");
                if(row.Revision==ProtocolBinary.MaxInteger)return new(Error:"revision_conflict");
                locked.Add(resource with{Row=row});
            }
            root=locked[0].Row;entry=locked[0].Entry;
            if(!await Scope(root,entry,authority))return new(Error:"not_found");
            error=RootError(root,operation,request,authority.Now);if(error is not null)return new(Error:error);
            if(!await NoLiveLinks(db,locked,ct))return new(Error:"persistence_unavailable");
            var plan=await TrashRestoreAssociations.BuildAsync(db,factory,request,account,authority.ProfileId,locked,missing,ct);
            // A single current database-time root CAS admits the whole operation.
            // Natural expiry after admission may complete under the held owner/job locks.
            var output=new List<TrashRestoredItem>();DateTimeOffset restoredAt=default;
            foreach(var resource in locked)
            {
                var row=resource.Row;var parent=plan.Parents.GetValueOrDefault((row.Kind,row.PublicId));var stamp=Guid.NewGuid();
                var parameters=TrashRestoreInventory.Parameters(row,parent,stamp).Concat(Base(request)).Concat(new object[]{
                    new NpgsqlParameter("operation",operation.PublicId),new NpgsqlParameter("kind",row.Kind),new NpgsqlParameter("snapshot",resource.Entry.AssociationSnapshot)}).ToArray();
                var restored=await db.Database.SqlQueryRaw<DateTimeOffset>(TrashRestoreInventory.RestoreSql(row.Kind,output.Count>0),parameters).ToListAsync(ct);
                if(restored.Count!=1){if(output.Count>0)return new(Error:"revision_conflict");authority=await AuthorityAsync(db,request,ct);return new(Error:AuthorityError(authority)??(authority.Now>=operation.PurgeAt?"trash_expired":"revision_conflict"));}
                if(output.Count==0)restoredAt=restored[0];
                var after=await TrashRestoreInventory.ReadAsync(db,row.Kind,account.Id,row.PublicId,ct);
                if(after is null||!TrashRestoreInventory.Preserved(row,after,parent,stamp))return new(Error:"persistence_unavailable");
                output.Add(new(row.Kind,row.PublicId,after.Revision,after.ServerSequence,after.EditedAt));
            }
            foreach(var parent in plan.Bumps.Values.OrderBy(x=>x.Kind,StringComparer.Ordinal).ThenBy(x=>x.PublicId))
                if(!await TrashRestoreInventory.BumpAsync(db,parent,ct))return new(Error:"persistence_unavailable");
            foreach(var link in plan.Links)AddLink(db,account.Id,link);
            await db.SaveChangesAsync(ct);
            foreach(var link in plan.Links)if(!await HasLink(db,link,ct))return new(Error:"persistence_unavailable");
            await db.TrashOperations.Where(x=>x.Id==operation.Id&&x.AccountId==account.Id).ExecuteDeleteAsync(ct);
            await db.RetentionWorkItems.Where(x=>x.OperationKey=="trash/"+operation.PublicId).ExecuteDeleteAsync(ct);
            if(await db.TrashEntries.AnyAsync(x=>x.OperationId==operation.Id,ct)||await db.TrashOperations.AnyAsync(x=>x.Id==operation.Id,ct)
                ||await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="trash/"+operation.PublicId,ct))return new(Error:"persistence_unavailable");
            await tx.CommitAsync(ct);
            return new(new(operation.PublicId,root.Kind,root.PublicId,restoredAt,output.OrderBy(x=>x.Kind,StringComparer.Ordinal).ThenBy(x=>x.ResourceId).ToArray(),plan.Omissions()));
            Task<bool> Terminal(TrashRestoreResource r)=>db.TerminalErasures.AnyAsync(x=>x.ResourceKind==r.Kind&&x.ResourceId==r.PublicId,ct);
            Task<bool> Scope(TrashRestoreResource r,TrashEntry e,Authority a)=>TrashRestoreAssociations.CanAccessRootAsync(db,factory,request,account.Id,a.ProfileId,r,e.AssociationSnapshot,ct);
        }
        catch(TrashRestoreConflictException){return new(Error:"revision_conflict");}
        catch(PostgresException e) when(e.SqlState=="2200H"){return new(Error:"revision_conflict");}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return new(Error:"persistence_unavailable");}
        catch(Exception e) when(e is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            ||e is InvalidOperationException{InnerException:DbException or DbUpdateException or TimeoutException}){return new(Error:"persistence_unavailable");}
    }
    private static bool ValidRetained(TrashRestoreResource row,TrashOperation op)=>row.DeletedAt==op.DeletedAt&&row.PurgeAt==op.PurgeAt
        &&TrashRestoreInventory.Time(row.EditedAt)&&TrashRestoreInventory.Safe(row.Revision)&&TrashRestoreInventory.Safe(row.ServerSequence);
    private static string? RootError(TrashRestoreResource root,TrashOperation op,TrashRestoreRequest request,DateTimeOffset now)
    {
        if(!TrashRestoreInventory.Time(op.DeletedAt)||!TrashRestoreInventory.Time(op.PurgeAt)||op.PurgeAt-op.DeletedAt!=TimeSpan.FromDays(30)||op.DeletedAt>now||!ValidRetained(root,op))return "persistence_unavailable";
        if(now>=op.PurgeAt)return "trash_expired";
        return root.Revision!=request.ExpectedRevision||root.Revision==ProtocolBinary.MaxInteger?"revision_conflict":null;
    }
    private static object[] Base(TrashRestoreRequest r)=>[new NpgsqlParameter("actor",r.Actor),new NpgsqlParameter("verifier",r.AccessVerifier)];
    private static async Task<Authority> AuthorityAsync(AppDbContext db,TrashRestoreRequest r,CancellationToken ct)=>
        (await db.Database.SqlQueryRaw<Authority>(TrashListSql.Authority+"""
            SELECT EXISTS(SELECT FROM actor a WHERE a.state=0 AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)) active,
              EXISTS(SELECT FROM session) allowed,(SELECT profile_id FROM session) profile_id,statement_timestamp() now
            """,Base(r)).ToListAsync(ct)).Single();
    private static string? AuthorityError(Authority a)=>!a.Active?"not_found":!a.Allowed?"vault_access_denied":null;
    private sealed class Authority{public bool Active{get;set;}public bool Allowed{get;set;}public long? ProfileId{get;set;}public DateTimeOffset Now{get;set;}}
    private static async Task<bool> NoLiveLinks(AppDbContext db,IReadOnlyList<RestoringTrashResource> resources,CancellationToken ct)
    {
        foreach(var r in resources.Select(x=>x.Row))
        {
            var live=r.Kind switch{
                "record"=>await db.ProfileRecords.AnyAsync(x=>x.RecordId==r.Id,ct)||await db.CollectionRecords.AnyAsync(x=>x.RecordId==r.Id,ct),
                "folder"=>await db.ProfileFolders.AnyAsync(x=>x.FolderId==r.Id,ct)||await db.CollectionFolders.AnyAsync(x=>x.FolderId==r.Id,ct),
                "collection"=>await db.ProfileCollections.AnyAsync(x=>x.CollectionId==r.Id,ct)||await db.CollectionRecords.AnyAsync(x=>x.CollectionId==r.Id,ct)||await db.CollectionFolders.AnyAsync(x=>x.CollectionId==r.Id,ct),
                _=>await db.ProfileRecords.AnyAsync(x=>x.ProfileId==r.Id,ct)||await db.ProfileFolders.AnyAsync(x=>x.ProfileId==r.Id,ct)||await db.ProfileCollections.AnyAsync(x=>x.ProfileId==r.Id,ct)};
            if(live)return false;
        }
        var folders=resources.Where(x=>x.Row.Kind=="folder").Select(x=>x.Row.Id).ToArray();
        var records=resources.Where(x=>x.Row.Kind=="record").Select(x=>x.Row.Id).ToArray();
        if(folders.Length>0&&(await db.Folders.AnyAsync(x=>x.AccountId==resources[0].Row.AccountId&&x.DeletedAt==null&&x.ParentFolderId!=null&&folders.Contains(x.ParentFolderId.Value)&&!folders.Contains(x.Id),ct)
            ||await db.Records.AnyAsync(x=>x.AccountId==resources[0].Row.AccountId&&x.DeletedAt==null&&x.FolderId!=null&&folders.Contains(x.FolderId.Value)&&!records.Contains(x.Id),ct)))return false;
        return true;
    }
    private static void AddLink(AppDbContext db,long owner,RestoredTrashLink l)
    {
        object entity=l.Kind switch{
            "profile_record"=>new ProfileRecord{AccountId=owner,ProfileId=l.Left,RecordId=l.Right},
            "profile_folder"=>new ProfileFolder{AccountId=owner,ProfileId=l.Left,FolderId=l.Right},
            "profile_collection"=>new ProfileCollection{ProfileId=l.Left,CollectionId=l.Right},
            "collection_record"=>new CollectionRecord{AccountId=owner,CollectionId=l.Left,RecordId=l.Right},
            "collection_folder"=>new CollectionFolder{AccountId=owner,CollectionId=l.Left,FolderId=l.Right},_=>throw new InvalidOperationException("Invalid retained link.")};db.Add(entity);
    }
    private static Task<bool> HasLink(AppDbContext db,RestoredTrashLink l,CancellationToken ct)=>l.Kind switch{
        "profile_record"=>db.ProfileRecords.AnyAsync(x=>x.ProfileId==l.Left&&x.RecordId==l.Right,ct),
        "profile_folder"=>db.ProfileFolders.AnyAsync(x=>x.ProfileId==l.Left&&x.FolderId==l.Right,ct),
        "profile_collection"=>db.ProfileCollections.AnyAsync(x=>x.ProfileId==l.Left&&x.CollectionId==l.Right,ct),
        "collection_record"=>db.CollectionRecords.AnyAsync(x=>x.CollectionId==l.Left&&x.RecordId==l.Right,ct),
        "collection_folder"=>db.CollectionFolders.AnyAsync(x=>x.CollectionId==l.Left&&x.FolderId==l.Right,ct),_=>throw new InvalidOperationException("Invalid retained link.")};
}
