using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Operations;

public sealed class RetentionWorkStore(IDbContextFactory<AppDbContext> factory) : IRetentionWorkStore
{
    public async Task<IReadOnlyList<Guid>> GetDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await context.Database.SqlQuery<Guid>($"""
            SELECT public_id AS "Value" FROM cerberus.retention_work_item
            WHERE completed_at IS NULL
              AND due_at<=CASE WHEN operation_key LIKE 'trash/%' OR operation_key LIKE 'account-purge/%'
                THEN statement_timestamp() ELSE {now} END
              AND (claim_expires_at IS NULL OR claim_expires_at<=CASE
                WHEN operation_key LIKE 'trash/%' OR operation_key LIKE 'account-purge/%'
                THEN statement_timestamp() ELSE {now} END)
            ORDER BY due_at,public_id LIMIT {limit}
            """).ToListAsync(cancellationToken);
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
        if (work is null || now.Offset!=TimeSpan.Zero) return null;
        if (IsExpiry(work.OperationKey))
            now=await context.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
        if (!work.TryClaim(token, now, lease)) return null;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RetentionClaim(work.PublicId, token, work.OperationKey, work.ClaimExpiresAt!.Value);
    }

    private static bool IsExpiry(string key)=>key.StartsWith("trash/",StringComparison.Ordinal)||key.StartsWith("account-purge/",StringComparison.Ordinal);

    public async Task<bool> TryCompleteAsync(RetentionClaim claim, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var rows = await context.RetentionWorkItems.FromSqlInterpolated(
            $"SELECT * FROM cerberus.retention_work_item WHERE public_id = {claim.WorkId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var work = rows.SingleOrDefault();
        if(work is not null && (work.OperationKey.StartsWith("record-purge/",StringComparison.Ordinal)||work.OperationKey.StartsWith("account-erase/",StringComparison.Ordinal)||work.OperationKey.StartsWith("trash-erase/",StringComparison.Ordinal)||IsExpiry(work.OperationKey)))
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
