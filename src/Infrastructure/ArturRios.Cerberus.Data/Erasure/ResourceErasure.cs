using System.Text.Json.Nodes;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace ArturRios.Cerberus.Data.Erasure;

internal static class ResourceErasure
{
    // Caller owns the transaction, canonical owner/resource locks and, for a
    // worker, the complete durable ledger set. Replay runs before traffic opens.
    internal static async Task RemoveAsync(AppDbContext db,string kind,Guid id,RetentionClaim? claim,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(id==Guid.Empty||kind is not("record" or "folder" or "collection" or "profile"))throw new InvalidOperationException("Invalid typed resource erasure.");
        if(kind=="record"){await RecordErasure.RemoveAsync(db,id,claim,ct);return;}
        async Task Fence(){if(claim is not null)await RecordErasure.RequireClaimAsync(db,claim,ct);if(!await db.TerminalErasures.AnyAsync(x=>x.ResourceKind==kind&&x.ResourceId==id,ct))throw new InvalidOperationException("Typed terminal intent is required.");}
        async Task<int> Mutate(string sql,params object[] values)
        {
            await Fence();var args=values.Concat(new object[]{new NpgsqlParameter("terminal_kind",kind),new NpgsqlParameter("terminal_id",id)}).ToList();
            var guard=" AND EXISTS(SELECT FROM cerberus.terminal_erasure t WHERE t.resource_kind=@terminal_kind AND t.resource_id=@terminal_id)";
            if(claim is not null){guard+=" AND EXISTS(SELECT FROM cerberus.retention_work_item w WHERE w.public_id=@work AND w.operation_key=@work_key AND w.claim_token=@token AND w.completed_at IS NULL AND w.claim_expires_at>statement_timestamp())";args.AddRange([new NpgsqlParameter("work",claim.WorkId),new NpgsqlParameter("work_key",claim.OperationKey),new NpgsqlParameter("token",claim.Token)]);}
            var statement=sql+guard;var changed=await db.Database.ExecuteSqlRawAsync(statement,args,ct);await Fence();return changed;
        }
        await Fence();var root=await ReadRoot();
        var query=db.TrashOperations.Where(o=>o.RootResourceKind==kind&&o.RootResourceId==id||db.TrashEntries.Any(e=>e.OperationId==o.Id&&e.ResourceKind==kind&&e.ResourceId==id));
        if(root is not null)query=query.Where(o=>o.AccountId==root.AccountId);
        var operations=await query.Select(o=>new{o.Id,o.PublicId}).ToListAsync(ct);var operationIds=operations.Select(x=>x.Id).ToArray();
        var sessions=new List<ErasureJson>();
        if(root is not null&&kind=="profile")
        {
            sessions=await db.Database.SqlQueryRaw<ErasureJson>(ResourceErasureSql.Sessions,new NpgsqlParameter("owner",root.AccountId),new NpgsqlParameter("parent",root.Id)).ToListAsync(ct);
            await Mutate("UPDATE cerberus.vault_access_session SET revoked=TRUE WHERE account_id=@owner AND profile_id=@parent AND NOT revoked",new NpgsqlParameter("owner",root.AccountId),new NpgsqlParameter("parent",root.Id));
        }
        if(root is not null&&kind=="folder")
        {
            var children=await db.Database.SqlQueryRaw<ErasureChild>(ResourceErasureSql.Children,new NpgsqlParameter("owner",root.AccountId),new NpgsqlParameter("parent",root.Id)).ToListAsync(ct);
            foreach(var child in children)
            {
                var before=JsonNode.Parse(child.Body)!.AsObject();var revision=before["revision"]!.GetValue<long>();var sequence=before["server_sequence"]!.GetValue<long>();
                if(revision is <=0 or >=ProtocolBinary.MaxInteger||sequence is <=0 or >ProtocolBinary.MaxInteger)throw new InvalidOperationException("Invalid surviving child metadata.");
                var stamp=Guid.NewGuid();
                if(await Mutate(ResourceErasureSql.Detach(child.Kind),new NpgsqlParameter("owner",root.AccountId),new NpgsqlParameter("id",child.ResourceId),new NpgsqlParameter("before",child.Body),new NpgsqlParameter("stamp",stamp))!=1)throw new InvalidOperationException("Surviving child changed.");
                var after=(await db.Database.SqlQueryRaw<ErasureChild>(ResourceErasureSql.Child,new NpgsqlParameter("owner",root.AccountId),new NpgsqlParameter("id",child.ResourceId),new NpgsqlParameter("kind",child.Kind)).ToListAsync(ct)).SingleOrDefault();
                if(after is null)throw new InvalidOperationException("Surviving child was removed.");
                var current=JsonNode.Parse(after.Body)!.AsObject();var parent=child.Kind=="record"?"folder_id":"parent_folder_id";
                if(current[parent] is not null||current["revision"]!.GetValue<long>()!=revision+1||current["server_sequence"]!.GetValue<long>()<=sequence
                    ||current["server_sequence"]!.GetValue<long>()>ProtocolBinary.MaxInteger||current["concurrency_stamp"]!.GetValue<Guid>()!=stamp)throw new InvalidOperationException("Invalid child detachment.");
                foreach(var name in new[]{parent,"revision","server_sequence","concurrency_stamp"}){before.Remove(name);current.Remove(name);}
                if(!JsonNode.DeepEquals(before,current))throw new InvalidOperationException("Surviving child bytes changed.");
            }
        }
        var removed=await Mutate(ResourceErasureSql.Delete(kind),new NpgsqlParameter("id",id));
        if(removed!=(root is null?0:1)||await ReadRoot() is not null)throw new InvalidOperationException("Physical erasure was not persisted.");
        foreach(var session in sessions)
        {
            var after=await db.Database.SqlQueryRaw<string>(ResourceErasureSql.Session,new NpgsqlParameter("id",session.Id)).SingleOrDefaultAsync(ct);
            if(after is null)throw new InvalidOperationException("Related session was removed.");
            var before=JsonNode.Parse(session.Body)!.AsObject();var current=JsonNode.Parse(after)!.AsObject();
            if(current["revoked"]?.GetValue<bool>()!=true)throw new InvalidOperationException("Related session was not revoked.");
            before.Remove("revoked");current.Remove("revoked");if(!JsonNode.DeepEquals(before,current))throw new InvalidOperationException("Selected session scope changed.");
        }
        await Mutate("DELETE FROM cerberus.trash_entry WHERE resource_kind=@terminal_kind AND resource_id=@terminal_id AND operation_id=ANY(@operations)",new NpgsqlParameter("operations",operationIds));
        if(await db.TrashEntries.AnyAsync(e=>operationIds.Contains(e.OperationId)&&e.ResourceKind==kind&&e.ResourceId==id,ct))throw new InvalidOperationException("Erased entry was retained.");
        foreach(var operation in operations)
        {
            if(await db.TrashEntries.AnyAsync(e=>e.OperationId==operation.Id,ct))continue;
            // An active original expiry claim must remain completable after the
            // operation disappears. Never remove this worker's own claim row.
            await Mutate("DELETE FROM cerberus.retention_work_item WHERE operation_key=@key AND (claim_token IS NULL OR completed_at IS NOT NULL) AND public_id<>@preserve",new NpgsqlParameter("key","trash/"+operation.PublicId),new NpgsqlParameter("preserve",claim?.WorkId??Guid.Empty));
            await Mutate("DELETE FROM cerberus.trash_operation WHERE id=@operation AND NOT EXISTS(SELECT FROM cerberus.trash_entry e WHERE e.operation_id=@operation)",new NpgsqlParameter("operation",operation.Id));
            if(await db.TrashOperations.AnyAsync(o=>o.Id==operation.Id,ct))throw new InvalidOperationException("Empty erasure operation was retained.");
        }
        Task<ErasureRoot?> ReadRoot()=>db.Database.SqlQueryRaw<ErasureRoot>(ResourceErasureSql.Root,new NpgsqlParameter("kind",kind),new NpgsqlParameter("id",id)).SingleOrDefaultAsync(ct);
    }
    private sealed class ErasureRoot{public long Id{get;set;}public long AccountId{get;set;}}
    private sealed class ErasureJson{public long Id{get;set;}public string Body{get;set;}="";}
    private sealed class ErasureChild{public string Kind{get;set;}="";public Guid ResourceId{get;set;}public string Body{get;set;}="";}
}
