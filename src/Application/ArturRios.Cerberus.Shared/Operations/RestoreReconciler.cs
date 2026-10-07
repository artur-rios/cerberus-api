using ArturRios.Cerberus.Domain.Operations;

namespace ArturRios.Cerberus.Shared.Operations;

public interface IRestoreAuthorizationVerifier
{
    Task<bool> VerifyAsync(CancellationToken cancellationToken);
}

public interface IRestoreReconciler
{
    Task<bool> ReconcileAsync(CancellationToken cancellationToken);
}

public sealed class RestoreTrafficGate
{
    private int _ready;
    public bool IsReady => Volatile.Read(ref _ready) == 1;
    internal void Close() => Interlocked.Exchange(ref _ready, 0);
    internal void Open() => Interlocked.Exchange(ref _ready, 1);
}

public sealed class RestoreReconciler(IErasureLedger ledger, ITerminalErasureStore erasures,
    IRestoreAuthorizationVerifier authorization, RestoreTrafficGate gate) : IRestoreReconciler
{
    private readonly SemaphoreSlim _reconciliation = new(1, 1);

    public async Task<bool> ReconcileAsync(CancellationToken cancellationToken)
    {
        await _reconciliation.WaitAsync(cancellationToken);
        gate.Close();
        try
        {
            await foreach (var entry in ledger.ReadAsync(cancellationToken))
                await erasures.ReapplyAsync(entry, cancellationToken);
            if (!await authorization.VerifyAsync(cancellationToken)) return false;
            gate.Open();
            return true;
        }
        finally { _reconciliation.Release(); }
    }
}
