using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Erasure;

internal static class AccountErasureExecution
{
    internal static async Task ExecuteAsync(IDbContextFactory<AppDbContext> factory,IErasureLedger ledger,RetentionClaim claim,Guid id,CancellationToken cancellationToken)
    {
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
