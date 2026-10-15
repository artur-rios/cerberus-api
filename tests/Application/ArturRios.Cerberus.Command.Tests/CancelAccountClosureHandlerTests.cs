using ArturRios.Cerberus.Command.Accounts;
using ArturRios.Cerberus.Domain.Accounts;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class CancelAccountClosureHandlerTests
{
    private static readonly Guid Actor=Guid.NewGuid(),Account=Guid.NewGuid();
    private static readonly DateTimeOffset Now=DateTimeOffset.FromUnixTimeSeconds(1800000000);
    private static CancelAccountClosureCommand Command(){var c=new CancelAccountClosureCommand{ExpectedRevision=2};c.SetContext(Actor,Now.ToUnixTimeSeconds());return c;}
    [UnitTheory][InlineData("actor","authentication_required")][InlineData("old","fresh_authentication_required")]
    [InlineData("future","fresh_authentication_required")][InlineData("revision","validation_failed")]
    public async Task GivenInvalidFreshCancellationAuthority_WhenHandling_ThenRejectBeforeStore(string invalid,string error)
    {
        var c=Command();c.SetContext(invalid=="actor"?Guid.Empty:Actor,Now.ToUnixTimeSeconds()+(invalid=="old"?-60:invalid=="future"?1:0));
        if(invalid=="revision")c.ExpectedRevision=0;
        var result=await new CancelAccountClosureHandler(new Mock<IAccountClosureCancellationStore>(MockBehavior.Strict).Object,new Clock()).HandleAsync(c);
        Assert.Contains(error,result.Errors);Assert.Null(result.Data);
    }
    [UnitFact]
    public async Task GivenCommittedCancellation_WhenHandling_ThenReturnMinimalActiveMetadata()
    {
        var result=await Handler(new(Details())).HandleAsync(Command());Assert.True(result.Success);Assert.Equal(Account,result.Data!.AccountId);
        Assert.Equal(3,result.Data.Revision);Assert.Equal(3,result.Data.RevocationGeneration);Assert.Equal(Now,result.Data.CancelledAt);
        Assert.DoesNotContain(result.Data.GetType().GetProperties(),p=>p.Name.Contains("Envelope")||p.Name.Contains("Token")||p.Name.Contains("Access"));
    }
    [UnitTheory][InlineData("not_found")][InlineData("revision_conflict")][InlineData("closure_expired")]
    [InlineData("fresh_authentication_required")][InlineData("persistence_unavailable")]
    public async Task GivenSafeCancellationFailure_WhenHandling_ThenReturnNoAccount(string error)
    {var result=await Handler(new(Error:error)).HandleAsync(Command());Assert.Contains(error,result.Errors);Assert.Null(result.Data);}
    [UnitTheory][InlineData("missing")][InlineData("revision")][InlineData("id")][InlineData("generation")]
    [InlineData("future")][InlineData("unknown")][InlineData("error-data")]
    public async Task GivenInconsistentCancellationResult_WhenHandling_ThenFailClosed(string kind)
    {
        var details=Details();details=kind switch {"revision"=>details with{Revision=4},"id"=>details with{AccountId=Guid.Empty},
            "generation"=>details with{RevocationGeneration=0},"future"=>details with{CancelledAt=Now.AddSeconds(1)},_=>details};
        var stored=kind=="missing"?new AccountClosureCancellationResult():kind=="unknown"?new(Error:"private-failure"):new(details,kind=="error-data"?"not_found":null);
        var result=await Handler(stored).HandleAsync(Command());Assert.Contains("persistence_unavailable",result.Errors);Assert.Null(result.Data);
    }
    [UnitFact]
    public async Task GivenCancelledCaller_WhenHandling_ThenPropagateBeforeStore()
    {using var ct=new CancellationTokenSource();ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(new()).HandleAsync(Command(),ct.Token));}
    private static AccountClosureCancellationDetails Details()=>new(Account,3,3,Now,Now);
    private static CancelAccountClosureHandler Handler(AccountClosureCancellationResult result)
    {
        var store=new Mock<IAccountClosureCancellationStore>(MockBehavior.Strict);store.Setup(x=>x.CancelAsync(It.Is<AccountClosureCancellationRequest>(r=>r.Actor==Actor&&r.ExpectedRevision==2&&r.AuthenticationIssuedAt==Now.ToUnixTimeSeconds()),It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return new(store.Object,new Clock());
    }
    private sealed class Clock:TimeProvider{public override DateTimeOffset GetUtcNow()=>Now;}
}
