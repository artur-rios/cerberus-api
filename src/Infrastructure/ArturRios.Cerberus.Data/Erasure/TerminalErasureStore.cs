using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Erasure;

public sealed class TerminalErasureStore(IDbContextFactory<AppDbContext> factory) : ITerminalErasureStore
{
    public async Task ReapplyAsync(ErasureEntry entry, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cerberus.terminal_erasure (resource_id, resource_kind, deleted_at) VALUES ({entry.ResourceId}, {entry.ResourceKind}, {entry.DeletedAt}) ON CONFLICT (resource_id) DO NOTHING", cancellationToken);
        var existing = await context.TerminalErasures.AsNoTracking().SingleAsync(x => x.ResourceId == entry.ResourceId, cancellationToken);
        if (existing.ResourceKind != entry.ResourceKind) throw new InvalidDataException("Conflicting terminal erasure resource kind.");
    }
    public async Task<bool> IsErasedAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await context.TerminalErasures.AsNoTracking().AnyAsync(x => x.ResourceId == resourceId, cancellationToken);
    }
}
