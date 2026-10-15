using ArturRios.Cerberus.Data.Trash;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Operations;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Erasure;

public sealed class TrashPurgeHandler(IDbContextFactory<AppDbContext> factory,IErasureLedger ledger):IRetentionHandler
{
    public string Kind=>"trash-erase";
    public async Task ExecuteAsync(RetentionClaim claim,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var parts=claim.OperationKey.Split('/');
        if(parts.Length!=2||parts[0]!=Kind||!Guid.TryParseExact(parts[1],"D",out var id)||id==Guid.Empty||parts[1]!=id.ToString("D"))throw new InvalidOperationException("Invalid trash erasure work.");
        IReadOnlyList<ErasureEntry> durable;
        await using(var db=await factory.CreateDbContextAsync(ct))
        {
            await using var tx=await db.Database.BeginTransactionAsync(ct);var owner=await LockOwner(db,id,ct);await RecordErasure.RequireClaimAsync(db,claim,ct);
            if(owner is null){await MissingOwner(db,id,ct);durable=[];}
            else {var rows=(await TrashErasureInventory.ReadAsync(db,owner.Value,ct)).Where(x=>x.DeletedAt is not null&&x.MarkerAt is not null).ToArray();TrashErasureInventory.Validate(rows);durable=rows.Select(x=>x.Entry()).ToArray();}
            await tx.CommitAsync(ct);
        }
        // No database transaction is held across durable filesystem I/O.
        foreach(var entry in durable)await ledger.RecordAsync(entry,ct);
        await using var purge=await factory.CreateDbContextAsync(ct);await using var physical=await purge.Database.BeginTransactionAsync(ct);
        var current=await LockOwner(purge,id,ct);await RecordErasure.RequireClaimAsync(purge,claim,ct);
        if(current is null){await MissingOwner(purge,id,ct);await physical.CommitAsync(ct);return;}
        await TrashErasureInventory.LockAsync(purge,current.Value,ct);await RecordErasure.RequireClaimAsync(purge,claim,ct);
        var inventory=await TrashErasureInventory.ReadAsync(purge,current.Value,ct);var targets=inventory.Where(x=>x.DeletedAt is not null&&x.MarkerAt is not null).ToArray();TrashErasureInventory.Validate(targets);
        var ids=durable.Select(x=>x.ResourceId).Distinct().ToArray();var markers=await purge.TerminalErasures.AsNoTracking().Where(x=>ids.Contains(x.ResourceId)).ToListAsync(ct);var evidence=markers.Select(x=>new ErasureEntry(x.ResourceId,x.ResourceKind,x.DeletedAt)).ToHashSet();
        if(durable.Any(x=>!evidence.Contains(x))||targets.Select(x=>x.Entry()).Except(durable).Any()
            ||await TrashErasureInventory.AnyTargetsAsync(purge,current.Value,durable,true,ct))throw new InvalidOperationException("Erasure ledger set changed.");
        if(await TrashErasureInventory.ActiveEntryAsync(purge,current.Value,ct))throw new InvalidOperationException("Retained operation state changed.");
        foreach(var row in targets.OrderBy(x=>x.Kind=="record"?0:x.Kind=="folder"?1:x.Kind=="collection"?2:3).ThenBy(x=>x.ResourceId))
            await ResourceErasure.RemoveAsync(purge,row.Kind,row.ResourceId,claim,ct);
        await TrashErasureMetadata.CleanupAsync(purge,current.Value,claim,ct);
        var remaining=await TrashErasureInventory.ReadAsync(purge,current.Value,ct);
        if(remaining.Any(x=>x.MarkerAt is not null)||await TrashErasureInventory.AnyTargetsAsync(purge,current.Value,durable,false,ct))throw new InvalidOperationException("Physical trash erasure is incomplete.");
        await RecordErasure.RequireClaimAsync(purge,claim,ct);await physical.CommitAsync(ct);
    }
    private static async Task<long?> LockOwner(AppDbContext db,Guid id,CancellationToken ct)
    {await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE public_id={id} FOR UPDATE",ct);return await db.Accounts.AsNoTracking().Where(x=>x.PublicId==id).Select(x=>(long?)x.Id).SingleOrDefaultAsync(ct);}
    private static async Task MissingOwner(AppDbContext db,Guid id,CancellationToken ct)
    {if(!await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==id,ct))throw new InvalidOperationException("Erasure owner is missing.");}
}
