using ArturRios.Cerberus.Data.Trash;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.Shared.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Erasure;

public sealed class TrashExpiryHandler(IDbContextFactory<AppDbContext> factory,IErasureLedger ledger):IRetentionHandler
{
    public string Kind=>"trash";
    public async Task ExecuteAsync(RetentionClaim claim,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var id=RetentionExpiry.Identity(claim,Kind);
        IReadOnlyList<ErasureEntry> durable;
        await using(var db=await factory.CreateDbContextAsync(ct))
        {
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            var selection=await SelectAsync(db,id,claim,ct);
            if(selection is null){await tx.CommitAsync(ct);return;}
            var now=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
            foreach(var row in selection.Targets)await TrashErasureInventory.AddMarkerAsync(db,row,now,ct,claim);
            durable=await EntriesAsync(db,selection,ct);
            await RecordErasure.RequireClaimAsync(db,claim,ct);await tx.CommitAsync(ct);
        }
        // A complete typed ledger set is durable before any physical deletion.
        // No database transaction or row lock spans filesystem I/O.
        foreach(var entry in durable)await ledger.RecordAsync(entry,ct);
        await using var purge=await factory.CreateDbContextAsync(ct);await using var physical=await purge.Database.BeginTransactionAsync(ct);
        var current=await SelectAsync(purge,id,claim,ct);
        if(current is null){await physical.CommitAsync(ct);return;}
        var entries=await EntriesAsync(purge,current,ct);
        if(entries.Except(durable).Any())throw new InvalidOperationException("Retained expiry ledger set changed.");
        foreach(var target in current.Targets.OrderBy(x=>x.Kind=="record"?0:x.Kind=="folder"?1:x.Kind=="collection"?2:3).ThenBy(x=>x.ResourceId))
            await ResourceErasure.RemoveAsync(purge,target.Kind,target.ResourceId,claim,ct);
        if(await TrashErasureInventory.AnyTargetsAsync(purge,current.Operation.AccountId,entries,false,ct))throw new InvalidOperationException("Retained expiry is incomplete.");
        if(await purge.TrashEntries.AnyAsync(x=>x.OperationId==current.Operation.Id,ct))throw new InvalidOperationException("Retained entries remain.");
        // Partial replay can leave an empty operation with a terminal root.
        await RecordErasure.RequireClaimAsync(purge,claim,ct);
        await purge.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM cerberus.trash_operation o WHERE o.id={current.Operation.Id} AND o.account_id={current.Operation.AccountId}
              AND NOT EXISTS(SELECT FROM cerberus.trash_entry e WHERE e.operation_id=o.id)
              AND EXISTS(SELECT FROM cerberus.retention_work_item w WHERE w.public_id={claim.WorkId} AND w.operation_key={claim.OperationKey}
                AND w.claim_token={claim.Token} AND w.completed_at IS NULL AND w.claim_expires_at>statement_timestamp())
            """,ct);
        if(await purge.TrashOperations.AnyAsync(x=>x.PublicId==id,ct))throw new InvalidOperationException("Retained operation remains.");
        await RecordErasure.RequireClaimAsync(purge,claim,ct);await physical.CommitAsync(ct);
    }
    private sealed record Selection(TrashOperation Operation,IReadOnlyList<TrashErasureTarget> Targets);
    private static async Task<Selection?> SelectAsync(AppDbContext db,Guid id,RetentionClaim claim,CancellationToken ct)
    {
        var owner=await(from op in db.TrashOperations.AsNoTracking() join a in db.Accounts.AsNoTracking() on op.AccountId equals a.Id
            where op.PublicId==id select new{a.Id,a.PublicId}).SingleOrDefaultAsync(ct);
        if(owner is null)
        {
            await RecordErasure.RequireClaimAsync(db,claim,ct);
            if(await db.TrashOperations.AnyAsync(x=>x.PublicId==id,ct))throw new InvalidOperationException("Retained owner changed.");
            return null;
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE public_id={owner.PublicId} FOR UPDATE",ct);
        await RecordErasure.RequireClaimAsync(db,claim,ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.trash_operation WHERE public_id={id} AND account_id={owner.Id} FOR UPDATE",ct);
        var operation=await db.TrashOperations.AsNoTracking().SingleOrDefaultAsync(x=>x.PublicId==id&&x.AccountId==owner.Id,ct);
        if(operation is null)return null;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.trash_entry WHERE operation_id={operation.Id} ORDER BY resource_kind,resource_id FOR UPDATE",ct);
        await TrashErasureInventory.LockAsync(db,owner.Id,ct);await RecordErasure.RequireClaimAsync(db,claim,ct);
        var now=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
        if(!RetentionExpiry.Due(operation.DeletedAt,operation.PurgeAt,now)||operation.RootResourceId==Guid.Empty
            ||operation.RootResourceKind is not("record" or "folder" or "collection" or "profile"))throw new InvalidOperationException("Invalid retained expiry deadline.");
        var entries=await db.TrashEntries.AsNoTracking().Where(x=>x.OperationId==operation.Id).ToListAsync(ct);
        var rootTerminal=await db.TerminalErasures.AnyAsync(x=>x.ResourceKind==operation.RootResourceKind&&x.ResourceId==operation.RootResourceId,ct);
        if(!entries.Any(x=>x.ResourceKind==operation.RootResourceKind&&x.ResourceId==operation.RootResourceId)&&!rootTerminal
            ||entries.Any(x=>x.ResourceId==Guid.Empty||x.ResourceKind is not("record" or "folder" or "collection" or "profile"))
            ||(operation.RootResourceKind!="folder"?entries.Count>1:entries.Any(x=>x.ResourceKind is not("folder" or "record"))))
            throw new InvalidOperationException("Invalid retained expiry members.");
        var targets=(await TrashErasureInventory.ReadOperationAsync(db,owner.Id,operation.Id,ct)).ToArray();
        TrashErasureInventory.Validate(targets);
        if(targets.Length!=entries.Count||targets.Any(x=>x.DeletedAt!=operation.DeletedAt||x.PurgeAt!=operation.PurgeAt))
            throw new InvalidOperationException("Retained expiry state changed.");
        return new(operation,targets);
    }
    private static async Task<IReadOnlyList<ErasureEntry>> EntriesAsync(AppDbContext db,Selection selection,CancellationToken ct)
    {
        var entries=new List<ErasureEntry>();
        var targets=selection.Targets.Select(x=>(x.Kind,x.ResourceId)).Append((selection.Operation.RootResourceKind,selection.Operation.RootResourceId)).Distinct();
        foreach(var (kind,id) in targets)
        {
            var marker=await db.TerminalErasures.AsNoTracking().SingleOrDefaultAsync(x=>x.ResourceKind==kind&&x.ResourceId==id,ct);
            if(marker is null)throw new InvalidOperationException("Typed retained expiry intent is required.");
            entries.Add(new(id,kind,marker.DeletedAt));
        }
        return entries;
    }
}
