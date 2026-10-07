using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Operations;
using ArturRios.Cerberus.WebApi.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace ArturRios.Cerberus.WebApi.Tests;

public sealed class RetentionWorkerTests
{
    [UnitFact]
    public async Task GivenDependencyFailure_WhenPollingWorker_ThenMonitorWithoutLoggingSensitiveException()
    {
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        using var logs = new SerilogLoggerFactory(logger);
        using var services = new ServiceCollection().AddSingleton<IRetentionWorkStore, UnavailableStore>()
            .AddSingleton<IRetentionExecutor>(new RetentionExecutor([])).AddSingleton(TimeProvider.System)
            .AddTransient<RetentionRunner>().BuildServiceProvider();
        var options = new CerberusOptions { RetentionInterval = "00:05:00", MaxPageSize = 100 };
        var worker = new RetentionWorker(services.GetRequiredService<IServiceScopeFactory>(), options, TimeProvider.System, logs.CreateLogger<RetentionWorker>());
        await worker.PollOnceAsync(default);
        var entry = Assert.Single(sink.Events);
        Assert.Contains("retention_dependency_unavailable", entry.RenderMessage());
        Assert.DoesNotContain("protected-database-connection", entry.RenderMessage());
        Assert.Null(entry.Exception);
        worker.Dispose();
    }

    private sealed class UnavailableStore : IRetentionWorkStore
    {
        public Task<IReadOnlyList<Guid>> GetDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) => throw new IOException("protected-database-connection");
        public Task<RetentionClaim?> TryClaimAsync(Guid workId, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryCompleteAsync(RetentionClaim claim, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
