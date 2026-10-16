using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Cerberus.Shared.Operations;
using Moq;

namespace ArturRios.Cerberus.Shared.Tests;

public sealed class OperationalHealthProbeTests
{
    [UnitTheory][InlineData(true,true,"healthy")][InlineData(true,false,"unhealthy")][InlineData(false,true,"unhealthy")][InlineData(false,false,"unhealthy")]
    public async Task GivenDependencyStates_WhenProbing_ThenReportBothAndOnlyFixedRedactedComponents(bool db,bool identity,string expected)
    {
        var invoked=0;var provider=new Mock<IHeimdallClient>();provider.Setup(x=>x.VerifyScopeAsync(It.IsAny<CancellationToken>())).Returns(()=>{Interlocked.Increment(ref invoked);return Task.FromResult(identity);});
        var database=new Database(_=>{Interlocked.Increment(ref invoked);return Task.FromResult(db);});
        var result=await new OperationalHealthProbe(database,provider.Object,new(),new(),TimeProvider.System).CheckAsync(default);
        Assert.Equal(2,invoked);Assert.Equal(expected,result.Status);Assert.Equal(new[]{"database","identity","restore"},result.Components.Select(x=>x.Name));
        Assert.Equal(new[]{db?"healthy":"unhealthy",identity?"healthy":"unhealthy","healthy"},result.Components.Select(x=>x.Status));Assert.All(result.Components,x=>Assert.True(x.ElapsedMilliseconds>=0));
    }
    [UnitTheory][InlineData(false)][InlineData(true)]
    public async Task GivenDependencyExceptionWithProtectedText_WhenProbing_ThenReturnOnlyUnhealthyState(bool databaseFailure)
    {
        var provider=new Mock<IHeimdallClient>();provider.Setup(x=>x.VerifyScopeAsync(It.IsAny<CancellationToken>())).Returns(()=>databaseFailure?Task.FromResult(true):throw new HttpRequestException("private-provider-address-password"));
        var database=new Database(_=>databaseFailure?throw new InvalidOperationException("private-database-host-password"):Task.FromResult(true));
        var result=await new OperationalHealthProbe(database,provider.Object,new(),new(),TimeProvider.System).CheckAsync(default);
        Assert.Equal("unhealthy",result.Status);var raw=System.Text.Json.JsonSerializer.Serialize(result);Assert.DoesNotContain("private-",raw);Assert.DoesNotContain("Exception",raw);
    }
    [UnitFact]
    public async Task GivenRestoreClosesWhileDependenciesWait_WhenProbing_ThenFinalGateWins()
    {
        var gate=new RestoreTrafficGate();Assert.True(await Reconcile(gate,true));var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider=new Mock<IHeimdallClient>();provider.Setup(x=>x.VerifyScopeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var probe=new OperationalHealthProbe(new Database(_=>{started.SetResult();return release.Task;}),provider.Object,gate,new(){RestoreRequired=true},TimeProvider.System);
        var pending=probe.CheckAsync(default);await started.Task.WaitAsync(TimeSpan.FromSeconds(2));Assert.False(await Reconcile(gate,false));release.SetResult(true);var result=await pending;
        Assert.Equal("unhealthy",result.Status);Assert.Equal("unhealthy",result.Components.Single(x=>x.Name=="restore").Status);
    }
    [UnitTheory][InlineData(false)][InlineData(true)]
    public async Task GivenRequiredRestoreGate_WhenProbing_ThenRequireSuccessfulReconciliation(bool open)
    {
        var gate=new RestoreTrafficGate();if(open)Assert.True(await Reconcile(gate,true));var provider=new Mock<IHeimdallClient>();provider.Setup(x=>x.VerifyScopeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var result=await new OperationalHealthProbe(new Database(_=>Task.FromResult(true)),provider.Object,gate,new(){RestoreRequired=true},TimeProvider.System).CheckAsync(default);
        Assert.Equal(open?"healthy":"unhealthy",result.Status);
    }
    [UnitTheory][InlineData(false)][InlineData(true)]
    public async Task GivenControlledDependencyTimeout_WhenProbing_ThenCancelWaitAndReportUnhealthy(bool identityStall)
    {
        var time=new ManualClock();var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var stopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Stall(CancellationToken ct){started.SetResult();try{await Task.Delay(Timeout.InfiniteTimeSpan,ct);return true;}finally{stopped.SetResult();}}
        var provider=new Mock<IHeimdallClient>();provider.Setup(x=>x.VerifyScopeAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken ct)=>identityStall?Stall(ct):Task.FromResult(true));
        var pending=new OperationalHealthProbe(new Database(ct=>identityStall?Task.FromResult(true):Stall(ct)),provider.Object,new(),new(),time).CheckAsync(default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));time.Advance(TimeSpan.FromSeconds(5));var result=await pending.WaitAsync(TimeSpan.FromSeconds(2));await stopped.Task;
        Assert.Equal("unhealthy",result.Status);Assert.Equal("unhealthy",result.Components.Single(x=>x.Name==(identityStall?"identity":"database")).Status);
    }
    [UnitFact]
    public async Task GivenCallerCancellationDuringBothDependencies_WhenProbing_ThenPropagateAndFinishBothWaits()
    {
        using var cancellation=new CancellationTokenSource();var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var entered=0;var exited=0;
        async Task<bool> Stall(CancellationToken ct){if(Interlocked.Increment(ref entered)==2)started.SetResult();try{await Task.Delay(Timeout.InfiniteTimeSpan,ct);return true;}finally{Interlocked.Increment(ref exited);}}
        var provider=new Mock<IHeimdallClient>();provider.Setup(x=>x.VerifyScopeAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken ct)=>Stall(ct));
        var pending=new OperationalHealthProbe(new Database(Stall),provider.Object,new(),new(),TimeProvider.System).CheckAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);Assert.Equal(2,exited);
    }
    private static Task<bool> Reconcile(RestoreTrafficGate gate,bool authorized)
    {var auth=new Mock<IRestoreAuthorizationVerifier>();auth.Setup(x=>x.VerifyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(authorized);return new RestoreReconciler(new EmptyLedger(),Mock.Of<ITerminalErasureStore>(),auth.Object,gate).ReconcileAsync(default);}
    private sealed class EmptyLedger:IErasureLedger
    {public Task RecordAsync(ErasureEntry e,CancellationToken ct)=>throw new InvalidOperationException();public async IAsyncEnumerable<ErasureEntry> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct){await Task.CompletedTask;ct.ThrowIfCancellationRequested();yield break;}}
    private sealed class Database(Func<CancellationToken,Task<bool>> check):IDatabaseReadinessProbe
    {public Task<bool> CheckAsync(CancellationToken ct)=>check(ct);}
    private sealed class ManualClock:TimeProvider
    {
        private long _ticks;private readonly List<ManualTimer> _timers=[];public override long TimestampFrequency=>TimeSpan.TicksPerSecond;public override long GetTimestamp()=>_ticks;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period){var t=new ManualTimer(callback,state,_ticks+dueTime.Ticks);_timers.Add(t);return t;}
        public void Advance(TimeSpan duration){_ticks+=duration.Ticks;foreach(var t in _timers.ToArray())if(!t.Disposed&&t.Due<=_ticks)t.Fire();}
        private sealed class ManualTimer(TimerCallback callback,object? state,long due):ITimer
        {public long Due=due;public bool Disposed;public void Fire(){Disposed=true;callback(state);}public bool Change(TimeSpan a,TimeSpan b)=>throw new NotSupportedException();public void Dispose()=>Disposed=true;public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}}
    }
}
