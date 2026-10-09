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
        if(work is not null && work.OperationKey.StartsWith("record-purge/",StringComparison.Ordinal))
        {
            if(work.OperationKey!=claim.OperationKey || claim.Token==Guid.Empty || now.Offset!=TimeSpan.Zero)return false;
            // The caller's clock cannot extend a purge lease. This statement starts
            // after the persisted claim-row wait and checks current database expiry.
            var changed=await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cerberus.retention_work_item SET completed_at=statement_timestamp(),concurrency_stamp={Guid.NewGuid()}
                WHERE public_id={claim.WorkId} AND operation_key={claim.OperationKey} AND claim_token={claim.Token}
                  AND completed_at IS NULL AND claim_expires_at>statement_timestamp()
                """,cancellationToken);
            if(changed!=1)return false;
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        if (work is null || work.OperationKey != claim.OperationKey
            || !work.TryComplete(claim.Token, now)) return false;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
