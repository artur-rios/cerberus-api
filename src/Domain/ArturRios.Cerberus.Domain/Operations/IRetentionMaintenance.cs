namespace ArturRios.Cerberus.Domain.Operations;

public sealed record RetentionPreparationResult(int ScheduledJobs,int ExpiredStreams);

/// <summary>Checks worker execution rights and prepares bounded internal retention work.</summary>
public interface IRetentionMaintenance
{
    Task<RetentionPreparationResult> PrepareAsync(int pageSize,CancellationToken cancellationToken);
}
