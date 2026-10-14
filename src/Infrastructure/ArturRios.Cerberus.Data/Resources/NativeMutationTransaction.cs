using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArturRios.Cerberus.Data.Resources;

// Standalone stores own their transaction. A synchronization batch owns the
// outer transaction; each native mutation keeps its original all-or-nothing
// behavior through a savepoint without committing the batch prematurely.
public sealed class NativeMutationTransaction : IAsyncDisposable
{
    private readonly IDbContextTransaction transaction;
    private readonly string? savepoint;
    private bool completed;
    private bool disposed;
    private NativeMutationTransaction(IDbContextTransaction transaction, string? savepoint)
    { this.transaction = transaction; this.savepoint = savepoint; }

    public static async Task<NativeMutationTransaction> BeginAsync(AppDbContext db, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var current = db.Database.CurrentTransaction;
        if (current is null) return new(await db.Database.BeginTransactionAsync(ct), null);
        if (!current.SupportsSavepoints) throw new InvalidOperationException("Native mutation requires savepoint support.");
        var name = "native_" + Guid.NewGuid().ToString("N");
        await current.CreateSavepointAsync(name, ct);
        return new(current, name);
    }
    public async Task CommitAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) throw new InvalidOperationException("Native mutation is already complete.");
        if (savepoint is null) await transaction.CommitAsync(ct);
        else await transaction.ReleaseSavepointAsync(savepoint, ct);
        completed = true;
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (savepoint is null) { await transaction.DisposeAsync(); return; }
        if (!completed)
        {
            // Cleanup must still roll back a caller-cancelled or failed statement.
            await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None);
            await transaction.ReleaseSavepointAsync(savepoint, CancellationToken.None);
        }
    }
}
