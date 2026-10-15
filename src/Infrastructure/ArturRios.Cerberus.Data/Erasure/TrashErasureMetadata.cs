using ArturRios.Cerberus.Data.Trash;
using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace ArturRios.Cerberus.Data.Erasure;

internal static class TrashErasureMetadata
{
    internal static async Task CleanupAsync(AppDbContext db,long owner,RetentionClaim claim,CancellationToken ct)
    {
        async Task Mutate(string sql,params object[] values)
        {
            await RecordErasure.RequireClaimAsync(db,claim,ct);
            var statement=sql+" AND EXISTS(SELECT FROM cerberus.retention_work_item w WHERE w.public_id=@work AND w.claim_token=@token AND w.operation_key=@work_key AND w.completed_at IS NULL AND w.claim_expires_at>statement_timestamp())";
            var args=values.Concat(new object[]{new NpgsqlParameter("work",claim.WorkId),new NpgsqlParameter("token",claim.Token),new NpgsqlParameter("work_key",claim.OperationKey)}).ToArray();await db.Database.ExecuteSqlRawAsync(statement,args,ct);
            await RecordErasure.RequireClaimAsync(db,claim,ct);
        }
        var remaining=(await TrashErasureInventory.ReadAsync(db,owner,ct)).Select(x=>(x.Kind,x.ResourceId)).ToHashSet();
        var operations=await db.TrashOperations.AsNoTracking().Where(x=>x.AccountId==owner).Select(x=>new{x.Id,x.PublicId}).ToListAsync(ct);
        foreach(var op in operations)
        {
            var entries=await db.TrashEntries.AsNoTracking().Where(x=>x.OperationId==op.Id).Select(x=>new{x.Id,x.ResourceKind,x.ResourceId}).ToListAsync(ct);
            foreach(var e in entries.Where(e=>!remaining.Contains((e.ResourceKind,e.ResourceId))))
                await Mutate("DELETE FROM cerberus.trash_entry WHERE id=@entry AND operation_id=@operation AND EXISTS(SELECT FROM cerberus.trash_operation o WHERE o.id=@operation AND o.account_id=@owner)",new NpgsqlParameter("entry",e.Id),new NpgsqlParameter("operation",op.Id),new NpgsqlParameter("owner",owner));
            if(await db.TrashEntries.AnyAsync(x=>x.OperationId==op.Id,ct))continue;
            await Mutate("DELETE FROM cerberus.retention_work_item WHERE operation_key=@key AND (claim_token IS NULL OR completed_at IS NOT NULL) AND public_id<>@preserve AND EXISTS(SELECT FROM cerberus.trash_operation o WHERE o.id=@operation AND o.account_id=@owner)",new NpgsqlParameter("key","trash/"+op.PublicId),new NpgsqlParameter("preserve",claim.WorkId),new NpgsqlParameter("operation",op.Id),new NpgsqlParameter("owner",owner));
            await Mutate("DELETE FROM cerberus.trash_operation WHERE id=@operation AND account_id=@owner AND NOT EXISTS(SELECT FROM cerberus.trash_entry e WHERE e.operation_id=@operation)",new NpgsqlParameter("operation",op.Id),new NpgsqlParameter("owner",owner));
            if(await db.TrashOperations.AnyAsync(x=>x.Id==op.Id,ct))throw new InvalidOperationException("Erased operation cleanup failed.");
        }
    }
}
