using ArturRios.Cerberus.Domain.Operations;

namespace ArturRios.Cerberus.Domain.Tests;

public class RetentionWorkItemTests
{
    private static readonly DateTimeOffset s_now = DateTimeOffset.Parse("2026-10-07T00:00:00Z");

    [UnitFact]
    public void GivenDueWork_WhenClaiming_ThenHoldAnExclusiveTimedLease()
    {
        var work = new RetentionWorkItem { DueAt = s_now };
        var claim = Guid.NewGuid();
        Assert.True(work.TryClaim(claim, s_now, TimeSpan.FromMinutes(1)));
        Assert.Equal(claim, work.ClaimToken);
        Assert.Equal(s_now.AddMinutes(1), work.ClaimExpiresAt);
        Assert.False(work.TryClaim(Guid.NewGuid(), s_now, TimeSpan.FromMinutes(1)));
        Assert.Equal(claim, work.ClaimToken);
    }

    [UnitFact]
    public void GivenExpiredLease_WhenRetrying_ThenReplaceClaimAndRejectOldCompletion()
    {
        var work = new RetentionWorkItem { DueAt = s_now };
        var old = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        Assert.True(work.TryClaim(old, s_now, TimeSpan.FromMinutes(1)));
        Assert.True(work.TryClaim(replacement, s_now.AddMinutes(1), TimeSpan.FromMinutes(1)));
        Assert.False(work.TryComplete(old, s_now.AddMinutes(1)));
        Assert.Null(work.CompletedAt);
        Assert.True(work.TryComplete(replacement, s_now.AddMinutes(1)));
        Assert.False(work.TryClaim(Guid.NewGuid(), s_now.AddMinutes(2), TimeSpan.FromMinutes(1)));
        Assert.Equal(2, work.Attempts);
    }

    [UnitFact]
    public void GivenUndueWorkOrInvalidDuration_WhenClaiming_ThenPreserveState()
    {
        var work = new RetentionWorkItem { DueAt = s_now.AddMinutes(1) };
        Assert.False(work.TryClaim(Guid.NewGuid(), s_now, TimeSpan.FromMinutes(1)));
        Assert.False(work.TryClaim(Guid.NewGuid(), s_now.AddMinutes(1), TimeSpan.Zero));
        Assert.Equal(0, work.Attempts);
        Assert.Null(work.ClaimToken);
    }

    [UnitFact]
    public void GivenIncompleteLeaseOrNonUtcClock_WhenCompleting_ThenPreserveUncompletedState()
    {
        var token = Guid.NewGuid();
        var work = new RetentionWorkItem { ClaimToken = token };
        Assert.False(work.TryComplete(token, s_now));
        work.ClaimExpiresAt = s_now.AddMinutes(1);
        Assert.False(work.TryComplete(token, s_now.ToOffset(TimeSpan.FromHours(1))));
        Assert.Null(work.CompletedAt);
    }

    [UnitFact]
    public void GivenExhaustedAttemptCounter_WhenClaiming_ThenRejectWithoutOverflow()
    {
        var work = new RetentionWorkItem { DueAt = s_now, Attempts = int.MaxValue };
        Assert.False(work.TryClaim(Guid.NewGuid(), s_now, TimeSpan.FromMinutes(1)));
        Assert.Equal(int.MaxValue, work.Attempts);
        Assert.Null(work.ClaimToken);
    }
}
