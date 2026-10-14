using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Tests;

public class AccountClosureCancellationTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-10-14T08:00:00Z");
    [UnitTheory][InlineData(0,false)][InlineData(-1,false)][InlineData(1,true)]
    [InlineData(9007199254740991,true)][InlineData(9007199254740992,false)]
    public void GivenCancellationRevision_WhenValidating_ThenRequirePositiveSafeInteger(long revision,bool valid)
        =>Assert.Equal(valid,new AccountClosureCancellationInput(revision).IsValid());
    [UnitTheory][InlineData("valid",true)][InlineData("id",false)][InlineData("revision",false)]
    [InlineData("generation",false)][InlineData("future",false)][InlineData("precision",false)][InlineData("offset",false)]
    public void GivenCancellationMetadata_WhenValidating_ThenBindSafeIdentityCountersAndUtcTimes(string kind,bool valid)
    {
        var value=new AccountClosureCancellationDetails(Guid.NewGuid(),3,3,Now,Now);
        value=kind switch {"id"=>value with {AccountId=Guid.Empty},"revision"=>value with {Revision=ProtocolBinary.MaxInteger+1},
            "generation"=>value with {RevocationGeneration=0},"future"=>value with {CancelledAt=Now.AddSeconds(1)},
            "precision"=>value with {CancelledAt=Now.AddTicks(-1)},"offset"=>value with {ServerTime=Now.ToOffset(TimeSpan.FromHours(1))},_=>value};
        Assert.Equal(valid,value.IsValid());
    }
    [UnitTheory][InlineData("valid",true)][InlineData("expired",false)][InlineData("boundary",false)]
    [InlineData("extended",false)][InlineData("id",false)][InlineData("revision",false)][InlineData("precision",false)]
    public void GivenRestrictedRecoveryMetadata_WhenValidating_ThenRequireUnexpiredBoundedClosure(string kind,bool valid)
    {
        var value=new AccountClosureRecoveryDetails(Guid.NewGuid(),2,Now.AddDays(29),Now);
        value=kind switch {"expired"=>value with {ClosureDeadline=Now.AddSeconds(-1)},"boundary"=>value with {ClosureDeadline=Now},
            "extended"=>value with {ClosureDeadline=Now.AddDays(30).AddSeconds(1)},"id"=>value with {AccountId=Guid.Empty},
            "revision"=>value with {Revision=0},"precision"=>value with {ServerTime=Now.AddTicks(1)},_=>value};
        Assert.Equal(valid,value.IsValid());
    }
}
