using System.Text.Json;
using ArturRios.Cerberus.Data.Folders;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Trash;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Trash;

internal sealed record RestoringTrashResource(TrashRestoreResource Row,TrashEntry Entry,TrashAssociations Associations);
internal sealed record RestoredTrashLink(string Kind,long Left,long Right);
internal sealed class TrashRestorePlan
{
    internal Dictionary<(string,Guid),long?> Parents{get;}=[];
    internal Dictionary<(string,Guid),TrashRestoreResource> Bumps{get;}=[];
    internal HashSet<RestoredTrashLink> Links{get;}=[];
    private readonly Dictionary<(string Kind,Guid Id,string Association),int> omissions=[];
    internal void Omit(TrashRestoreResource row,string association,int count=1)
    {if(count>0){var key=(row.Kind,row.PublicId,association);omissions[key]=checked(omissions.GetValueOrDefault(key)+count);}}
    internal TrashRestoreOmission[] Omissions()=>omissions.OrderBy(x=>x.Key.Kind,StringComparer.Ordinal).ThenBy(x=>x.Key.Id).ThenBy(x=>x.Key.Association,StringComparer.Ordinal)
        .Select(x=>new TrashRestoreOmission(x.Key.Kind,x.Key.Id,x.Key.Association,x.Value)).ToArray();
}
internal static class TrashRestoreAssociations
{
    internal static async Task<bool> CanAccessRootAsync(AppDbContext db,IDbContextFactory<AppDbContext> factory,TrashRestoreRequest request,
        long account,long? selection,TrashRestoreResource root,byte[] snapshot,CancellationToken ct)
    {
        if(selection is null)return true;
        if(root.Kind=="profile" || !TrashAssociations.TryRead(root.Kind,snapshot,out var associations))return false;
        var selected=await db.Profiles.Where(x=>x.Id==selection&&x.AccountId==account).Select(x=>x.PublicId).SingleAsync(ct);
        if(associations!.ProfileIds.Contains(selected))return true;
        if(await SelectedCollections(db,account,selection).AnyAsync(x=>associations.CollectionIds.Contains(x.PublicId),ct))return true;
        return associations.Parent is {} parent && await VisibleOwnedFolder(db,factory,request,account,parent,ct);
    }
    private static IQueryable<ArturRios.Cerberus.Domain.Resources.VaultCollection> SelectedCollections(AppDbContext db,long account,long? selection)=>
        db.Collections.Where(c=>c.AccountId==account&&c.DeletedAt==null&&c.PurgeAt==null
            && !db.TerminalErasures.Any(t=>t.ResourceKind=="collection"&&t.ResourceId==c.PublicId)
            && db.ProfileCollections.Any(pc=>pc.ProfileId==selection&&pc.CollectionId==c.Id));
    private static async Task<bool> VisibleOwnedFolder(AppDbContext db,IDbContextFactory<AppDbContext> factory,TrashRestoreRequest request,long account,Guid id,CancellationToken ct)
    {
        if(!await db.Folders.AnyAsync(x=>x.AccountId==account&&x.PublicId==id&&x.DeletedAt==null&&x.PurgeAt==null,ct))return false;
        var read=await new FolderReadStore(new TransactionContextFactory(factory,db)).ReadAsync(new(request.Actor,request.AccessVerifier,id),ct);
        if(read.Error=="persistence_unavailable")throw new JsonException("Required retained ancestry is invalid.");
        return read.Error is null && read.Data is not null;
    }
    internal static async Task<TrashRestorePlan> BuildAsync(AppDbContext db,IDbContextFactory<AppDbContext> factory,TrashRestoreRequest request,
        Account account,long? selection,IReadOnlyList<RestoringTrashResource> resources,int missing,CancellationToken ct)
    {
        var plan=new TrashRestorePlan();var root=resources[0].Row;
        plan.Omit(root,"resource",missing);
        var restored=resources.ToDictionary(x=>(x.Row.Kind,x.Row.PublicId));
        var cache=new Dictionary<(string,Guid),TrashRestoreResource?>();
        var graphRows=await db.Folders.AsNoTracking().Where(x=>x.AccountId==account.Id).Select(x=>new FolderNode(x.Id,x.PublicId,x.ParentFolderId,x.DeletedAt!=null||x.PurgeAt!=null)).ToListAsync(ct);
        var relevantIds=resources.SelectMany(x=>x.Associations.ProfileIds.Concat(x.Associations.CollectionIds).Concat(x.Associations.RecordIds)
            .Concat(x.Associations.FolderIds).Concat(x.Associations.Parent is {} parent?[parent]:[]).Append(x.Row.PublicId)).Concat(graphRows.Select(x=>x.PublicId)).Distinct().ToArray();
        var terminal=(await db.TerminalErasures.AsNoTracking().Where(x=>relevantIds.Contains(x.ResourceId)).Select(x=>new{x.ResourceKind,x.ResourceId}).ToListAsync(ct)).Select(x=>(x.ResourceKind,x.ResourceId)).ToHashSet();
        var graph=graphRows.ToDictionary(x=>x.Id);var selectedId=selection is null?(Guid?)null:await db.Profiles.Where(x=>x.Id==selection&&x.AccountId==account.Id).Select(x=>(Guid?)x.PublicId).SingleAsync(ct);
        var selectedCollections=selection is null?null:(await SelectedCollections(db,account.Id,selection).Select(x=>x.PublicId).ToArrayAsync(ct)).ToHashSet();
        bool Complete(long? parent)
        {
            var visited=new HashSet<long>();var hidden=false;var cycle=false;
            while(parent is {} id){if(!graph.TryGetValue(id,out var f))return false;if(!visited.Add(id)){cycle=true;break;}hidden|=f.Hidden||terminal.Contains(("folder",f.PublicId));parent=f.Parent;}
            if(hidden)return false;if(cycle)throw new JsonException("Invalid relevant folder ancestry.");return true;
        }
        async Task<TrashRestoreResource?> Active(string kind,Guid id)
        {
            if(terminal.Contains((kind,id)))return null;
            var key=(kind,id);if(!cache.TryGetValue(key,out var row)){row=await TrashRestoreInventory.ReadAsync(db,kind,account.Id,id,ct);cache.Add(key,row);}
            if(row is null||row.DeletedAt is not null||row.PurgeAt is not null)return null;
            if(kind is "record" or "folder" && !Complete(kind=="folder"?row.Id:row.Parent))return null;
            if(!TrashRestoreInventory.Safe(row.Revision)||!TrashRestoreInventory.Safe(row.ServerSequence)||!TrashRestoreInventory.Time(row.EditedAt))throw new JsonException("Invalid required association metadata.");
            return row;
        }
        void Bump(TrashRestoreResource row){if(!restored.ContainsKey((row.Kind,row.PublicId)))plan.Bumps.TryAdd((row.Kind,row.PublicId),row);}
        async Task Profiles(TrashRestoreResource row,Guid[] ids)
        {
            foreach(var id in ids){var p=await Active("profile",id);if(p is null||selectedId is {} selected&&id!=selected){plan.Omit(row,"profile");continue;}
                plan.Links.Add(new(row.Kind=="record"?"profile_record":row.Kind=="folder"?"profile_folder":"profile_collection",p.Id,row.Id));Bump(p);}
        }
        foreach(var resource in resources)
        {
            var row=resource.Row;var a=resource.Associations;
            if(row.Kind is "record" or "folder")
            {
                long? parent=null;
                if(a.Parent is {} original)
                {
                    if(restored.TryGetValue(("folder",original),out var internalParent))parent=internalParent.Row.Id;
                    else {var external=await Active("folder",original);
                        if(external is not null&&(selection is null||await VisibleOwnedFolder(db,factory,request,account.Id,original,ct))){parent=external.Id;Bump(external);}else plan.Omit(row,"parent");}
                }
                plan.Parents.Add((row.Kind,row.PublicId),parent);
                await Profiles(row,a.ProfileIds);
                foreach(var id in a.CollectionIds){var c=await Active("collection",id);if(c is null||selectedCollections is not null&&!selectedCollections.Contains(id)){plan.Omit(row,"collection");continue;}
                    plan.Links.Add(new(row.Kind=="record"?"collection_record":"collection_folder",c.Id,row.Id));Bump(c);}
            }
            else if(row.Kind=="collection")
            {
                await Profiles(row,a.ProfileIds);
                foreach(var (kind,ids) in new[]{("record",a.RecordIds),("folder",a.FolderIds)})
                    foreach(var id in ids){var item=await Active(kind,id);if(item is null){plan.Omit(row,kind);continue;}
                        if(!await TrashRestoreInventory.NativeAsync(db,item,account.PublicId,request.Actor,ct))throw new JsonException("Invalid required native member.");
                        plan.Links.Add(new("collection_"+kind,row.Id,item.Id));Bump(item);}
            }
            else
            {
                foreach(var (kind,ids) in new[]{("record",a.RecordIds),("folder",a.FolderIds)})
                    foreach(var id in ids){var item=await Active(kind,id);if(item is null){plan.Omit(row,kind);continue;}plan.Links.Add(new("profile_"+kind,row.Id,item.Id));}
                foreach(var id in a.CollectionIds.Order())
                {
                    var resolved=await ProfileAssociationResolver.ResolveAsync(db,account.Id,request.Actor,[],[],[id],ct);
                    if(resolved.Error=="not_found"){plan.Omit(row,"collection");continue;}
                    if(resolved.Error is not null||resolved.Collections.Length!=1)throw new JsonException("Invalid required collection authority.");
                    plan.Links.Add(new("profile_collection",row.Id,resolved.Collections[0]));
                }
            }
        }
        // Original internal edges must also form a complete acyclic resulting graph.
        foreach(var resource in resources.Where(x=>x.Row.Kind=="folder"))
            graph[resource.Row.Id]=new(resource.Row.Id,resource.Row.PublicId,plan.Parents[("folder",resource.Row.PublicId)],false);
        foreach(var resource in resources.Where(x=>x.Row.Kind is "folder" or "record"))
            if(!Complete(resource.Row.Kind=="folder"?resource.Row.Id:plan.Parents[(resource.Row.Kind,resource.Row.PublicId)]))throw new JsonException("Invalid resulting retained topology.");
        if(plan.Bumps.Values.Any(x=>x.Revision==ArturRios.Cerberus.Domain.Protection.ProtocolBinary.MaxInteger))throw new TrashRestoreConflictException();
        return plan;
    }
    private sealed record FolderNode(long Id,Guid PublicId,long? Parent,bool Hidden);
}
internal sealed class TrashRestoreConflictException:Exception;
