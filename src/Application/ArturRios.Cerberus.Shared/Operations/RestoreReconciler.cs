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
    private readonly object _state = new();
    private long _generation;
    private bool _ready;
    public bool IsReady { get { lock (_state) return _ready; } }
    internal long Close() { lock (_state) { _ready = false; return ++_generation; } }
    internal bool Open(long generation)
    {
        lock (_state)
        {
            if (_generation != generation) return false;
            _ready = true;
            return true;
        }
    }
}

public sealed class RestoreReconciler(IErasureLedger ledger, ITerminalErasureStore erasures,
    IRestoreAuthorizationVerifier authorization, RestoreTrafficGate gate) : IRestoreReconciler
{
    private readonly SemaphoreSlim _reconciliation = new(1, 1);

    public async Task<bool> ReconcileAsync(CancellationToken cancellationToken)
    {
        var generation = gate.Close();
        await _reconciliation.WaitAsync(cancellationToken);
        try
        {
            await foreach (var entry in ledger.ReadAsync(cancellationToken))
                await erasures.ReapplyAsync(entry, cancellationToken);
            if (!await authorization.VerifyAsync(cancellationToken)) return false;
            return gate.Open(generation);
        }
        finally { _reconciliation.Release(); }
    }
}
