using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Erasure;

public sealed class TerminalErasureStore(IDbContextFactory<AppDbContext> factory) : ITerminalErasureStore
{
    public async Task ReapplyAsync(ErasureEntry entry, CancellationToken cancellationToken)
    {
        if (!TerminalResourceIdentity.IsValid(entry.ResourceKind, entry.ResourceId)
            || entry.DeletedAt.Offset != TimeSpan.Zero || entry.DeletedAt < DateTimeOffset.UnixEpoch)
            throw new InvalidDataException("Invalid terminal erasure.");
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cerberus.terminal_erasure (resource_id, resource_kind, deleted_at)
            VALUES ({entry.ResourceId}, {entry.ResourceKind}, {entry.DeletedAt})
            ON CONFLICT (resource_kind,resource_id) DO UPDATE
            SET deleted_at=LEAST(cerberus.terminal_erasure.deleted_at,EXCLUDED.deleted_at)
            """, cancellationToken);
        if (entry.ResourceKind == "record") await RecordErasure.RemoveAsync(context, entry.ResourceId, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    public async Task<bool> IsErasedAsync(string resourceKind, Guid resourceId, CancellationToken cancellationToken)
    {
        if (!TerminalResourceIdentity.IsValid(resourceKind, resourceId))
            throw new InvalidDataException("Invalid terminal identity.");
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await context.TerminalErasures.AsNoTracking().AnyAsync(x => x.ResourceKind == resourceKind && x.ResourceId == resourceId, cancellationToken);
    }
}
