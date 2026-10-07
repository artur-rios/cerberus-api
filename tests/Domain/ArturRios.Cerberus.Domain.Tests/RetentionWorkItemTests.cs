using ArturRios.Cerberus.Domain.Operations;

namespace ArturRios.Cerberus.Domain.Tests;

public class RetentionWorkItemTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-07T00:00:00Z");

    [UnitFact]
    public void GivenDueWork_WhenClaiming_ThenHoldAnExclusiveTimedLease()
    {
        var work = new RetentionWorkItem { DueAt = Now };
        var claim = Guid.NewGuid();
        Assert.True(work.TryClaim(claim, Now, TimeSpan.FromMinutes(1)));
        Assert.Equal(claim, work.ClaimToken);
        Assert.Equal(Now.AddMinutes(1), work.ClaimExpiresAt);
        Assert.False(work.TryClaim(Guid.NewGuid(), Now, TimeSpan.FromMinutes(1)));
        Assert.Equal(claim, work.ClaimToken);
    }

    [UnitFact]
    public void GivenExpiredLease_WhenRetrying_ThenReplaceClaimAndRejectOldCompletion()
    {
        var work = new RetentionWorkItem { DueAt = Now };
        var old = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        Assert.True(work.TryClaim(old, Now, TimeSpan.FromMinutes(1)));
        Assert.True(work.TryClaim(replacement, Now.AddMinutes(1), TimeSpan.FromMinutes(1)));
        Assert.False(work.TryComplete(old, Now.AddMinutes(1)));
        Assert.Null(work.CompletedAt);
        Assert.True(work.TryComplete(replacement, Now.AddMinutes(1)));
        Assert.False(work.TryClaim(Guid.NewGuid(), Now.AddMinutes(2), TimeSpan.FromMinutes(1)));
        Assert.Equal(2, work.Attempts);
    }

    [UnitFact]
    public void GivenUndueWorkOrInvalidDuration_WhenClaiming_ThenPreserveState()
    {
        var work = new RetentionWorkItem { DueAt = Now.AddMinutes(1) };
        Assert.False(work.TryClaim(Guid.NewGuid(), Now, TimeSpan.FromMinutes(1)));
        Assert.False(work.TryClaim(Guid.NewGuid(), Now.AddMinutes(1), TimeSpan.Zero));
        Assert.Equal(0, work.Attempts);
        Assert.Null(work.ClaimToken);
    }
}
