using ArturRios.Cerberus.Domain.Operations;

namespace ArturRios.Cerberus.Shared.Operations;

/// <summary>Handlers must be idempotent and fence mutations with the persisted claim token.</summary>
public interface IRetentionExecutor
{
    Task ExecuteAsync(RetentionClaim claim, CancellationToken cancellationToken);
}

public sealed record RetentionRunResult(int Completed, int Failed)
{
    public int ScheduledJobs{get;init;}
    public int ExpiredStreams{get;init;}
}

public sealed class RetentionRunner(IRetentionWorkStore store, IRetentionExecutor executor, TimeProvider clock,IRetentionMaintenance? maintenance=null)
{
    public async Task<RetentionRunResult> RunOnceAsync(TimeSpan lease, int pageSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        if(lease<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(lease));
        var preparation=maintenance is null?new RetentionPreparationResult(0,0):await maintenance.PrepareAsync(pageSize,cancellationToken);
        if(preparation.ScheduledJobs<0||preparation.ExpiredStreams<0)throw new InvalidOperationException("Invalid retention preparation result.");
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
        return new RetentionRunResult(completed, failed){ScheduledJobs=preparation.ScheduledJobs,ExpiredStreams=preparation.ExpiredStreams};
    }
}
