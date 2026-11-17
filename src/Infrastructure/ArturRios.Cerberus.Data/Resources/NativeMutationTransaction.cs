using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArturRios.Cerberus.Data.Resources;

// Standalone stores own their transaction. A synchronization batch owns the
// outer transaction; each native mutation keeps its original all-or-nothing
// behavior through a savepoint without committing the batch prematurely.
public sealed class NativeMutationTransaction : IAsyncDisposable
{
    private readonly IDbContextTransaction _transaction;
    private readonly string? _savepoint;
    private bool _completed;
    private bool _disposed;
    private NativeMutationTransaction(IDbContextTransaction transaction, string? savepoint)
    { this._transaction = transaction; this._savepoint = savepoint; }

    public static async Task<NativeMutationTransaction> BeginAsync(AppDbContext db, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var current = db.Database.CurrentTransaction;
        if (current is null)
            return new(await db.Database.BeginTransactionAsync(ct), null);
        if (!current.SupportsSavepoints)
            throw new InvalidOperationException("Native mutation requires savepoint support.");
        var name = "native_" + Guid.NewGuid().ToString("N");
        await current.CreateSavepointAsync(name, ct);
        return new(current, name);
    }
    public async Task CommitAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            throw new InvalidOperationException("Native mutation is already complete.");
        if (_savepoint is null)
            await _transaction.CommitAsync(ct);
        else
            await _transaction.ReleaseSavepointAsync(_savepoint, ct);
        _completed = true;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_savepoint is null)
        { await _transaction.DisposeAsync(); return; }
        if (!_completed)
        {
            // Cleanup must still roll back a caller-cancelled or failed statement.
            await _transaction.RollbackToSavepointAsync(_savepoint, CancellationToken.None);
            await _transaction.ReleaseSavepointAsync(_savepoint, CancellationToken.None);
        }
    }
}
