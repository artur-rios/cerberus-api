using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Operations;

namespace ArturRios.Cerberus.Shared.Tests;

public sealed class RetentionExecutorTests
{
    [UnitFact]
    public async Task GivenUnknownOperationKind_WhenExecuting_ThenRejectInsteadOfCompletingWork()
    {
        var executor = new RetentionExecutor([]);
        var claim = new RetentionClaim(Guid.NewGuid(), Guid.NewGuid(), "unsupported/fixture", DateTimeOffset.UtcNow.AddMinutes(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(claim, default));
    }

    [UnitFact]
    public async Task GivenKnownOperationKind_WhenExecuting_ThenDispatchTheExactFencedClaim()
    {
        var handler = new Handler();
        var executor = new RetentionExecutor([handler]);
        var claim = new RetentionClaim(Guid.NewGuid(), Guid.NewGuid(), "fixture/resource", DateTimeOffset.UtcNow.AddMinutes(5));
        await executor.ExecuteAsync(claim, default);
        Assert.Same(claim, handler.Received);
    }

    [UnitFact]
    public async Task GivenAmbiguousOperationHandlers_WhenExecuting_ThenNeverChooseAnArbitraryHandler()
    {
        var handlers = new[] { new Handler(), new Handler() };
        var claim = new RetentionClaim(Guid.NewGuid(), Guid.NewGuid(), "fixture/resource", DateTimeOffset.UtcNow.AddMinutes(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RetentionExecutor(handlers).ExecuteAsync(claim, default));
        Assert.All(handlers, handler => Assert.Null(handler.Received));
    }

    private sealed class Handler : IRetentionHandler
    {
        public string Kind => "fixture";
        public RetentionClaim? Received { get; private set; }
        public Task ExecuteAsync(RetentionClaim claim, CancellationToken cancellationToken) { Received = claim; return Task.CompletedTask; }
    }
}
