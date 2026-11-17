using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Operations;

namespace ArturRios.Cerberus.Shared.Tests;

public sealed class RetentionRunnerPreparationTests
{
    [UnitTheory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public async Task GivenInvalidPassBounds_WhenRunning_ThenRejectBeforeDiscoveringOrWriting(int seconds, int pageSize)
    {
        var store = new EmptyStore();
        var runner = new RetentionRunner(store, new Executor(), TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunOnceAsync(TimeSpan.FromSeconds(seconds), pageSize, default));
        Assert.False(store._discovered);
    }
    [UnitFact]
    public async Task GivenPreparation_WhenRunning_ThenPrepareBeforeDiscoveryAndReportCounts()
    {
        var preparation = new Preparation(new(3, 5));
        var store = new EmptyStore { _beforeDiscovery = () => Assert.True(preparation._prepared) };
        var result = await new RetentionRunner(store, new Executor(), TimeProvider.System, preparation).RunOnceAsync(TimeSpan.FromMinutes(1), 17, default);
        Assert.Equal(0, result.Completed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(3, result.ScheduledJobs);
        Assert.Equal(5, result.ExpiredStreams);
        Assert.Equal(17, preparation._pageSize);
    }
    [UnitFact]
    public async Task GivenDeniedPreparation_WhenRunning_ThenRefuseBeforeDiscoveringWork()
    {
        var store = new EmptyStore();
        var preparation = new Preparation(new(0, 0)) { _failure = new UnauthorizedAccessException() };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new RetentionRunner(store, new Executor(), TimeProvider.System, preparation).RunOnceAsync(TimeSpan.FromMinutes(1), 5, default));
        Assert.False(store._discovered);
    }
    [UnitTheory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public async Task GivenInvalidPreparationCounts_WhenRunning_ThenFailClosedBeforeClaiming(int jobs, int streams)
    {
        var store = new EmptyStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RetentionRunner(store, new Executor(), TimeProvider.System, new Preparation(new(jobs, streams))).RunOnceAsync(TimeSpan.FromMinutes(1), 5, default));
        Assert.False(store._discovered);
    }
    [UnitFact]
    public async Task GivenCancellation_WhenRunning_ThenNeverPrepareOrDiscover()
    {
        var store = new EmptyStore();
        var preparation = new Preparation(new(0, 0));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RetentionRunner(store, new Executor(), TimeProvider.System, preparation).RunOnceAsync(TimeSpan.FromMinutes(1), 5, cancellation.Token));
        Assert.False(preparation._prepared);
        Assert.False(store._discovered);
    }
    [UnitFact]
    public async Task GivenCancelledPreparation_WhenRunning_ThenPropagateAndDoNotDiscover()
    {
        var store = new EmptyStore();
        using var cancellation = new CancellationTokenSource();
        var preparation = new Preparation(new(0, 0)) { _before = () => { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RetentionRunner(store, new Executor(), TimeProvider.System, preparation).RunOnceAsync(TimeSpan.FromMinutes(1), 5, cancellation.Token));
        Assert.False(store._discovered);
    }
    private sealed class Preparation(RetentionPreparationResult result) : IRetentionMaintenance
    {
        internal bool _prepared; internal int _pageSize; internal Exception? _failure; internal Action? _before;
        public Task<RetentionPreparationResult> PrepareAsync(int pageSize, CancellationToken ct)
        { _before?.Invoke(); if (_failure is not null) throw _failure; ct.ThrowIfCancellationRequested(); _prepared = true; _pageSize = pageSize; return Task.FromResult(result); }
    }
    private sealed class EmptyStore : IRetentionWorkStore
    {
        internal bool _discovered; internal Action? _beforeDiscovery;
        public Task<IReadOnlyList<Guid>> GetDueAsync(DateTimeOffset now, int limit, CancellationToken ct) { _beforeDiscovery?.Invoke(); _discovered = true; return Task.FromResult<IReadOnlyList<Guid>>([]); }
        public Task<RetentionClaim?> TryClaimAsync(Guid id, DateTimeOffset now, TimeSpan lease, CancellationToken ct) => throw new InvalidOperationException();
        public Task<bool> TryCompleteAsync(RetentionClaim claim, DateTimeOffset now, CancellationToken ct) => throw new InvalidOperationException();
    }
    private sealed class Executor : IRetentionExecutor
    { public Task ExecuteAsync(RetentionClaim claim, CancellationToken ct) => throw new InvalidOperationException(); }
}
