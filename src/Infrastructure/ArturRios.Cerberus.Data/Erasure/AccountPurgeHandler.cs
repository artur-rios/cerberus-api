using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Erasure;

public sealed class AccountPurgeHandler(IDbContextFactory<AppDbContext> factory,IErasureLedger ledger):IRetentionHandler
{
    public string Kind=>"account-erase";
    public async Task ExecuteAsync(RetentionClaim claim,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();var parts=claim.OperationKey.Split('/');
        if(parts.Length!=2||parts[0]!=Kind||!Guid.TryParseExact(parts[1],"D",out var id)||id==Guid.Empty||parts[1]!=id.ToString("D"))throw new InvalidOperationException("Invalid account erasure work.");
        IReadOnlyList<ErasureEntry> entries;
        await using(var db=await factory.CreateDbContextAsync(cancellationToken))
        {
            await using var tx=await db.Database.BeginTransactionAsync(cancellationToken);
            await AccountErasure.LockAsync(db,id,cancellationToken);
            await RecordErasure.RequireClaimAsync(db,claim,cancellationToken);
            entries=await AccountErasure.EntriesAsync(db,id,cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        // Every retained typed identifier is durable before any physical removal.
        foreach(var entry in entries)await ledger.RecordAsync(entry,cancellationToken);
        await using var purge=await factory.CreateDbContextAsync(cancellationToken);await using var transaction=await purge.Database.BeginTransactionAsync(cancellationToken);
        await AccountErasure.RemoveAsync(purge,id,claim,cancellationToken,entries);
        await transaction.CommitAsync(cancellationToken);
    }
}
