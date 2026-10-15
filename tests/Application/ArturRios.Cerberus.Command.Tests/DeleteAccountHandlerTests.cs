using ArturRios.Cerberus.Command.Accounts;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Access;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class DeleteAccountHandlerTests
{
    private static readonly Guid Actor=Guid.NewGuid(),Account=Guid.NewGuid();
    private static readonly DateTimeOffset Now=DateTimeOffset.FromUnixTimeSeconds(1800000000);
    private static readonly string Access=Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static DeleteAccountCommand Command(){var c=new DeleteAccountCommand{ExpectedRevision=1};c.SetContext(Actor,Now.ToUnixTimeSeconds(),Access);return c;}
    [UnitTheory][InlineData("actor","authentication_required")][InlineData("old","fresh_authentication_required")]
    [InlineData("future","fresh_authentication_required")][InlineData("revision","validation_failed")]
    [InlineData("missing","vault_access_required")][InlineData("malformed","validation_failed")]
    public async Task GivenInvalidDeletionAuthority_WhenHandling_ThenRejectBeforeStore(string kind,string error)
    {
        var c=Command();c.SetContext(kind=="actor"?Guid.Empty:Actor,Now.ToUnixTimeSeconds()+(kind=="old"?-60:kind=="future"?1:0),kind=="missing"?null:kind=="malformed"?"invalid":Access);
        if(kind=="revision")c.ExpectedRevision=0;
        var result=await new DeleteAccountHandler(new Mock<IAccountDeletionStore>(MockBehavior.Strict).Object,new Clock()).HandleAsync(c);
        Assert.Contains(error,result.Errors);Assert.Null(result.Data);
    }
    [UnitTheory][InlineData("completed","account_deleted",200)][InlineData("pending","account_deletion_pending",202)]
    public async Task GivenAcceptedDeletion_WhenHandling_ThenReturnMinimalTrackedState(string state,string message,int status)
    {
        var result=await Handler(new(new(Account,Now,state))).HandleAsync(Command());Assert.True(result.Success);Assert.Contains(message,result.Messages);
        Assert.Equal(Account,result.Data!.AccountId);Assert.Equal(state,result.Data.State);Assert.Equal(Now,result.Data.DeletedAt);Assert.Equal(status,AccountDeletionMessages.StatusCodes[message]);
        Assert.DoesNotContain(result.Data.GetType().GetProperties(),x=>x.Name.Contains("Token")||x.Name.Contains("Access")||x.Name.Contains("Envelope"));
    }
    [UnitTheory][InlineData("not_found")][InlineData("revision_conflict")][InlineData("persistence_unavailable")]
    [InlineData("fresh_authentication_required")][InlineData("vault_access_denied")]
    public async Task GivenSafeDeletionFailure_WhenHandling_ThenReturnNoData(string error)
    {var r=await Handler(new(Error:error)).HandleAsync(Command());Assert.Contains(error,r.Errors);Assert.Null(r.Data);}
    [UnitTheory][InlineData("missing")][InlineData("id")][InlineData("state")][InlineData("time")][InlineData("unknown")][InlineData("mixed")]
    public async Task GivenInconsistentErasureResult_WhenHandling_ThenFailClosed(string kind)
    {
        var d=new AccountDeletionDetails(Account,Now,"completed");d=kind switch{"id"=>d with{AccountId=Guid.Empty},"state"=>d with{State="active"},"time"=>d with{DeletedAt=Now.AddTicks(1)},_=>d};
        var result=kind=="missing"?new AccountDeletionResult():kind=="unknown"?new(Error:"private-failure"):new(d,kind=="mixed"?"not_found":null);
        var r=await Handler(result).HandleAsync(Command());Assert.Contains("persistence_unavailable",r.Errors);Assert.Null(r.Data);
    }
    [UnitFact]public async Task GivenCancelledCaller_WhenDeleting_ThenPropagateBeforeStore()
    {using var ct=new CancellationTokenSource();ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(new()).HandleAsync(Command(),ct.Token));}
    private static DeleteAccountHandler Handler(AccountDeletionResult value)
    {
        OpaqueAccessHandle.TryHash(Access,out var verifier);var store=new Mock<IAccountDeletionStore>(MockBehavior.Strict);
        store.Setup(x=>x.DeleteAsync(It.Is<AccountDeletionRequest>(r=>r.Actor==Actor&&r.AuthenticationIssuedAt==Now.ToUnixTimeSeconds()&&r.ExpectedRevision==1&&r.AccessVerifier==verifier),It.IsAny<CancellationToken>())).ReturnsAsync(value);
        return new(store.Object,new Clock());
    }
    private sealed class Clock:TimeProvider{public override DateTimeOffset GetUtcNow()=>Now;}
}
