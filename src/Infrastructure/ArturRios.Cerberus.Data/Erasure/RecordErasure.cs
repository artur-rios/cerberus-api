using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.Data.Erasure;

internal static class RecordErasure
{
    // The caller owns the transaction. Restore callers run with traffic closed;
    // worker callers hold and recheck the persisted claim before every mutation.
    internal static async Task RemoveAsync(AppDbContext db, Guid id, RetentionClaim? claim, CancellationToken ct)
    {
        async Task Fence()
        {
            if (claim is not null) await RequireClaimAsync(db, claim, ct);
            if (!await db.TerminalErasures.AnyAsync(x => x.ResourceKind == "record" && x.ResourceId == id, ct))
                throw new InvalidOperationException("Terminal record intent is required.");
        }
        async Task Delete(string sql,params object[] parameters)
        {
            await Fence();
            // Expiry is tested by this mutation's statement timestamp, not a
            // preceding SELECT. The row lock also fences concurrent token theft.
            var guard=" AND EXISTS(SELECT FROM cerberus.terminal_erasure terminal WHERE terminal.resource_kind='record' AND terminal.resource_id=@terminal_id)";
            var args=parameters.Append<object>(new NpgsqlParameter("terminal_id",id)).ToList();
            if(claim is not null)
            {
                guard+=" AND EXISTS(SELECT FROM cerberus.retention_work_item authority WHERE authority.public_id=@work_id AND authority.claim_token=@claim_token AND authority.operation_key=@work_key AND authority.completed_at IS NULL AND authority.claim_expires_at>statement_timestamp())";
                args.AddRange([new NpgsqlParameter("work_id",claim.WorkId),new NpgsqlParameter("claim_token",claim.Token),new NpgsqlParameter("work_key",claim.OperationKey)]);
            }
            var statement=sql+guard;
            await db.Database.ExecuteSqlRawAsync(statement,args,ct);
            // A fenced zero-row idempotent delete must not acknowledge a lost claim.
            await Fence();
        }
        await Fence();
        var operations = await db.TrashOperations.Where(x => x.RootResourceKind == "record" && x.RootResourceId == id)
            .Select(x => new { x.Id, x.PublicId }).ToListAsync(ct);
        await Delete("DELETE FROM cerberus.record WHERE public_id=@id",new NpgsqlParameter("id",id));
        // Typed record associations cascade from the physical record FK.
        await Delete("DELETE FROM cerberus.trash_entry WHERE resource_kind='record' AND resource_id=@id",new NpgsqlParameter("id",id));
        foreach (var operation in operations)
        {
            if (await db.TrashEntries.AnyAsync(x => x.OperationId == operation.Id, ct)) continue;
            await Delete("DELETE FROM cerberus.retention_work_item WHERE operation_key=@trash_key AND NOT EXISTS(SELECT FROM cerberus.trash_entry e WHERE e.operation_id=@operation_id)",
                new NpgsqlParameter("trash_key","trash/"+operation.PublicId),new NpgsqlParameter("operation_id",operation.Id));
            await Delete("DELETE FROM cerberus.trash_operation WHERE id=@operation_id AND NOT EXISTS(SELECT FROM cerberus.trash_entry e WHERE e.operation_id=@operation_id)",new NpgsqlParameter("operation_id",operation.Id));
        }
    }

    internal static async Task RequireClaimAsync(AppDbContext db, RetentionClaim claim, CancellationToken ct)
    {
        // A persisted row lock prevents token replacement during a purge transaction.
        // Rechecking database time still rejects expiry while the lock is held.
        var valid = await db.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value" FROM cerberus.retention_work_item
            WHERE public_id={claim.WorkId} AND claim_token={claim.Token}
              AND operation_key={claim.OperationKey} AND completed_at IS NULL
              AND claim_expires_at>statement_timestamp() FOR UPDATE
            """).ToListAsync(ct);
        if (valid.Count != 1) throw new InvalidOperationException("Purge claim is unavailable.");
    }
}
