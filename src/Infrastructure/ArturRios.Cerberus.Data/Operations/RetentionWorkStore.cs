using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Operations;

public sealed class RetentionWorkStore(IDbContextFactory<AppDbContext> factory) : IRetentionWorkStore
{
    public async Task<IReadOnlyList<Guid>> GetDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await context.RetentionWorkItems.AsNoTracking()
            .Where(x => x.CompletedAt == null && x.DueAt <= now && (x.ClaimExpiresAt == null || x.ClaimExpiresAt <= now))
            .OrderBy(x => x.DueAt).ThenBy(x => x.PublicId).Take(limit).Select(x => x.PublicId).ToListAsync(cancellationToken);
    }

    public async Task<RetentionClaim?> TryClaimAsync(Guid workId, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var rows = await context.RetentionWorkItems.FromSqlInterpolated(
            $"SELECT * FROM cerberus.retention_work_item WHERE public_id = {workId} FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);
        var work = rows.SingleOrDefault();
        var token = Guid.NewGuid();
        if (work is null || !work.TryClaim(token, now, lease)) return null;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RetentionClaim(work.PublicId, token, work.OperationKey, work.ClaimExpiresAt!.Value);
    }

    public async Task<bool> TryCompleteAsync(RetentionClaim claim, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var rows = await context.RetentionWorkItems.FromSqlInterpolated(
            $"SELECT * FROM cerberus.retention_work_item WHERE public_id = {claim.WorkId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var work = rows.SingleOrDefault();
        if (work is null || work.OperationKey != claim.OperationKey
            || !work.TryComplete(claim.Token, now)) return false;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
