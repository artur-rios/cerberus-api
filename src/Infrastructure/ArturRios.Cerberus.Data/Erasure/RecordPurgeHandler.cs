using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Erasure;

public sealed class RecordPurgeHandler(IDbContextFactory<AppDbContext> factory, IErasureLedger ledger) : IRetentionHandler
{
    public string Kind => "record-purge";

    public async Task ExecuteAsync(RetentionClaim claim, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parts = claim.OperationKey.Split('/');
        if (parts.Length != 2 || parts[0] != Kind || !Guid.TryParseExact(parts[1], "D", out var id)
            || id == Guid.Empty || parts[1] != id.ToString("D"))
            throw new InvalidOperationException("Invalid record purge work.");
        DateTimeOffset deleted;
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            await RecordErasure.RequireClaimAsync(db, claim, cancellationToken);
            var intent = await db.TerminalErasures.AsNoTracking().SingleOrDefaultAsync(x => x.ResourceKind == "record" && x.ResourceId == id, cancellationToken);
            if (intent is null) throw new InvalidOperationException("Terminal record intent is required.");
            deleted = intent.DeletedAt;
            await tx.CommitAsync(cancellationToken);
        }
        await ledger.RecordAsync(new(id, "record", deleted), cancellationToken);
        await using var purge = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await purge.Database.BeginTransactionAsync(cancellationToken);
        await RecordErasure.RemoveAsync(purge, id, claim, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
