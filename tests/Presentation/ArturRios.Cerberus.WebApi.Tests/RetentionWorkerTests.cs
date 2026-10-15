using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
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

    [UnitFact]
    public async Task GivenDeniedExecutionRights_WhenPollingWorker_ThenRefuseBeforeDiscoveryAndRedactReason()
    {
        var sink=new CapturingSink();using var logger=new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();using var logs=new SerilogLoggerFactory(logger);
        using var services=new ServiceCollection().AddSingleton<IRetentionWorkStore,UnavailableStore>().AddSingleton<IRetentionExecutor>(new RetentionExecutor([])).AddSingleton(TimeProvider.System).AddSingleton<IRetentionMaintenance,DeniedPreparation>().AddTransient<RetentionRunner>().BuildServiceProvider();
        using var worker=new RetentionWorker(services.GetRequiredService<IServiceScopeFactory>(),new CerberusOptions{RetentionInterval="00:05:00",MaxPageSize=100},TimeProvider.System,logs.CreateLogger<RetentionWorker>());await worker.PollOnceAsync(default);
        var entry=Assert.Single(sink.Events);Assert.Contains("retention_execution_denied",entry.RenderMessage());Assert.DoesNotContain("protected-worker-rights-credential",entry.RenderMessage());Assert.Null(entry.Exception);
    }
    [UnitFact]
    public async Task GivenClosedRequiredRestoreBarrier_WhenPollingWorker_ThenNeverDiscoverOrLogACompletedPass()
    {
        var sink=new CapturingSink();using var logger=new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();using var logs=new SerilogLoggerFactory(logger);
        using var services=new ServiceCollection().AddSingleton<RestoreTrafficGate>().BuildServiceProvider();
        using var worker=new RetentionWorker(services.GetRequiredService<IServiceScopeFactory>(),new CerberusOptions{RestoreRequired=true,RetentionInterval="00:05:00",MaxPageSize=100},TimeProvider.System,logs.CreateLogger<RetentionWorker>());await worker.PollOnceAsync(default);Assert.False(services.GetRequiredService<RestoreTrafficGate>().IsReady);Assert.Empty(sink.Events);
    }
    [UnitFact]
    public async Task GivenNoDueWork_WhenPollingWorker_ThenPublishZeroProgressWithoutProtectedTags()
    {
        var counts=new ConcurrentDictionary<string,(long Value,int Tags)>();using var listener=new MeterListener();listener.InstrumentPublished=(instrument,current)=>{if(instrument.Meter.Name=="ArturRios.Cerberus.Retention")current.EnableMeasurementEvents(instrument);};listener.SetMeasurementEventCallback<long>((instrument,value,tags,_)=>counts[instrument.Name]=(value,tags.Length));listener.Start();
        var sink=new CapturingSink();using var logger=new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();using var logs=new SerilogLoggerFactory(logger);
        using var services=new ServiceCollection().AddSingleton<IRetentionWorkStore,EmptyRetentionStore>().AddSingleton<IRetentionExecutor>(new RetentionExecutor([])).AddSingleton(TimeProvider.System).AddTransient<RetentionRunner>().BuildServiceProvider();
        using var worker=new RetentionWorker(services.GetRequiredService<IServiceScopeFactory>(),new CerberusOptions{RetentionInterval="00:05:00",MaxPageSize=100},TimeProvider.System,logs.CreateLogger<RetentionWorker>());await worker.PollOnceAsync(default);
        foreach(var name in new[]{"cerberus.retention.completed","cerberus.retention.scheduled","cerberus.retention.expired_streams"}){Assert.True(counts.TryGetValue(name,out var measurement));Assert.Equal(0,measurement.Value);Assert.Equal(0,measurement.Tags);}Assert.Empty(sink.Events);
    }
    private sealed class DeniedPreparation:IRetentionMaintenance
    {public Task<RetentionPreparationResult> PrepareAsync(int pageSize,CancellationToken ct)=>throw new UnauthorizedAccessException("protected-worker-rights-credential");}
    private sealed class EmptyRetentionStore:IRetentionWorkStore
    {
        public Task<IReadOnlyList<Guid>> GetDueAsync(DateTimeOffset now,int limit,CancellationToken ct)=>Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<RetentionClaim?> TryClaimAsync(Guid id,DateTimeOffset now,TimeSpan lease,CancellationToken ct)=>throw new IOException("Unexpected claim");
        public Task<bool> TryCompleteAsync(RetentionClaim claim,DateTimeOffset now,CancellationToken ct)=>throw new IOException("Unexpected completion");
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
