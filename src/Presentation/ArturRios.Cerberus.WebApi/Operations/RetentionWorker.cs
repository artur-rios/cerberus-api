using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Operations;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace ArturRios.Cerberus.WebApi.Operations;

public sealed class RetentionWorker(IServiceScopeFactory scopes, CerberusOptions options, TimeProvider clock,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    private static readonly Meter Meter = new("ArturRios.Cerberus.Retention");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("cerberus.retention.failures");

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        if (options.RestoreRequired == true && !scope.ServiceProvider.GetRequiredService<RestoreTrafficGate>().IsReady) return;
        try
        {
            var result = await scope.ServiceProvider.GetRequiredService<RetentionRunner>()
                .RunOnceAsync(TimeSpan.ParseExact(options.RetentionInterval!, "c", CultureInfo.InvariantCulture), options.MaxPageSize, cancellationToken);
            if (result.Failed > 0)
            {
                Failures.Add(result.Failed);
                logger.LogWarning("Retention work failed. Code={Code}; Count={Count}", "retention_execution_failed", result.Failed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            Failures.Add(1);
            logger.LogWarning("Retention polling failed. Code={Code}", "retention_dependency_unavailable");
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.ParseExact(options.RetentionInterval!, "c", CultureInfo.InvariantCulture), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await PollOnceAsync(stoppingToken);
    }
}
