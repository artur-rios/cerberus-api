using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("Retention runner PostgreSQL")]
public sealed class RetentionRunnerTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenFailingExecutor_WhenRetryingAfterLease_ThenPreserveWorkUntilSuccessfulCompletion()
    {
        var clock = new TestClock(DateTimeOffset.Parse("2020-01-01T00:00:00Z"));
        var id = Guid.NewGuid();
        await using (var context = fixture.CreateContext())
        {
            context.Add(new RetentionWorkItem { PublicId = id, DueAt = clock.GetUtcNow(), OperationKey = "fixture/" + id });
            await context.SaveChangesAsync();
        }
        var executor = new TestExecutor();
        var runner = new RetentionRunner(new RetentionWorkStore(fixture), executor, clock);
        var first = await runner.RunOnceAsync(TimeSpan.FromMinutes(5), 100, default);
        Assert.Equal(1, first.Failed);
        await using (var verification = fixture.CreateContext())
        {
            var pending = await verification.RetentionWorkItems.SingleAsync(x => x.PublicId == id);
            Assert.Null(pending.CompletedAt);
            Assert.Equal(1, pending.Attempts);
        }
        Assert.Equal(new RetentionRunResult(0, 0), await runner.RunOnceAsync(TimeSpan.FromMinutes(5), 100, default));
        clock.Advance(TimeSpan.FromMinutes(5));
        executor.Fail = false;
        Assert.Equal(new RetentionRunResult(1, 0), await runner.RunOnceAsync(TimeSpan.FromMinutes(5), 100, default));
        await using var completedContext = fixture.CreateContext();
        var completed = await completedContext.RetentionWorkItems.SingleAsync(x => x.PublicId == id);
        Assert.Equal(clock.GetUtcNow(), completed.CompletedAt);
        Assert.Equal(2, completed.Attempts);
        Assert.Equal(new RetentionRunResult(0, 0), await runner.RunOnceAsync(TimeSpan.FromMinutes(5), 100, default));
    }

    [FunctionalFact]
    public async Task GivenCancelledRun_WhenPolling_ThenPropagateCancellation()
    {
        var runner = new RetentionRunner(new RetentionWorkStore(fixture), new TestExecutor(), TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunOnceAsync(TimeSpan.FromMinutes(5), 100, cancellation.Token));
    }

    private sealed class TestExecutor : IRetentionExecutor
    {
        public bool Fail { get; set; } = true;
        public Task ExecuteAsync(RetentionClaim claim, CancellationToken cancellationToken) =>
            Fail ? throw new IOException("upstream-secret-must-not-be-logged") : Task.CompletedTask;
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}

[CollectionDefinition("Retention runner PostgreSQL", DisableParallelization = true)]
public sealed class RetentionRunnerPostgresCollection : ICollectionFixture<PostgresFixture>;
