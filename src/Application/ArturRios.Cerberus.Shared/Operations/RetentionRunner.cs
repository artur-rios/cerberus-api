using ArturRios.Cerberus.Domain.Operations;

namespace ArturRios.Cerberus.Shared.Operations;

/// <summary>Handlers must be idempotent and fence mutations with the persisted claim token.</summary>
public interface IRetentionExecutor
{
    Task ExecuteAsync(RetentionClaim claim, CancellationToken cancellationToken);
}

public sealed record RetentionRunResult(int Completed, int Failed);

public sealed class RetentionRunner(IRetentionWorkStore store, IRetentionExecutor executor, TimeProvider clock)
{
    public async Task<RetentionRunResult> RunOnceAsync(TimeSpan lease, int pageSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completed = 0;
        var failed = 0;
        var due = await store.GetDueAsync(clock.GetUtcNow(), pageSize, cancellationToken);
        foreach (var id in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claim = await store.TryClaimAsync(id, clock.GetUtcNow(), lease, cancellationToken);
            if (claim is null) continue;
            try
            {
                await executor.ExecuteAsync(claim, cancellationToken);
                if (await store.TryCompleteAsync(claim, clock.GetUtcNow(), cancellationToken)) completed++;
                else failed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { failed++; } // Dependency exceptions may contain protected data.
        }
        return new RetentionRunResult(completed, failed);
    }
}
