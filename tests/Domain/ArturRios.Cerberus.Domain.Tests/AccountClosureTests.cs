using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;

namespace ArturRios.Cerberus.Domain.Tests;

public class AccountClosureTests
{
    private static readonly DateTimeOffset s_now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    [UnitTheory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(59, true)]
    [InlineData(60, false)]
    [InlineData(61, false)]
    public void GivenSignedAuthenticationAge_WhenRequestingClosure_ThenRequireCurrentSixtySecondWindow(int age, bool valid)
    { Assert.Equal(valid, FreshAuthentication.IsFresh(s_now.ToUnixTimeSeconds() - age, s_now)); }

    [UnitTheory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void GivenUnsupportedAuthenticationTime_WhenClosing_ThenReject(long issued)
    { Assert.False(FreshAuthentication.IsFresh(issued, s_now)); }

    [UnitTheory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(9007199254740991, true)]
    [InlineData(9007199254740992, false)]
    public void GivenExpectedAccountRevision_WhenValidatingClosure_ThenRequireSafePositiveCounter(long revision, bool valid)
    { Assert.Equal(valid, new AccountClosureInput(revision).IsValid()); }

    [UnitTheory]
    [InlineData("identifier")]
    [InlineData("revision")]
    [InlineData("generation")]
    [InlineData("offset")]
    [InlineData("precision")]
    [InlineData("window")]
    [InlineData("future")]
    public void GivenMalformedRetainedDeadline_WhenValidatingClosureResult_ThenFailClosed(string invalid)
    {
        var result = new AccountClosureDetails(Guid.NewGuid(), 2, 2, s_now, s_now.AddDays(30), s_now, true);
        Assert.True(result.IsValid());
        result = invalid switch
        {
            "identifier" => result with { AccountId = Guid.Empty },
            "revision" => result with { Revision = ProtocolBinary.MaxInteger + 1 },
            "generation" => result with { RevocationGeneration = 0 },
            "offset" => result with { ClosureRequestedAt = s_now.ToOffset(TimeSpan.FromHours(1)) },
            "precision" => result with { ClosureDeadline = s_now.AddDays(30).AddTicks(1) },
            "window" => result with { ClosureDeadline = s_now.AddDays(31) },
            _ => result with { ServerTime = s_now.AddSeconds(-1) }
        };
        Assert.False(result.IsValid());
    }
}
