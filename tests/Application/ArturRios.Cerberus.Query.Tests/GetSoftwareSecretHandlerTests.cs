using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Cerberus.Query.Software;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Query.Tests;

public class GetSoftwareSecretHandlerTests
{
    [UnitTheory][InlineData("record",false)][InlineData("collection",false)][InlineData("record",true)]
    public async Task GivenCurrentLiveIdentityAndBoundEncryptedTarget_WhenGetting_ThenReturnOnlyRequestedCiphertextAndSelectedProvisioning(string kind,bool member)
    {
        var c=SoftwareSecretFixture.Create(kind,member);using var ct=new CancellationTokenSource();
        var live=new Mock<IHeimdallApplicationClient>(MockBehavior.Strict);live.Setup(x=>x.AuthenticateAsync("original-bearer",ct.Token)).ReturnsAsync(new HeimdallApplicationAuthentication(c.Identity));
        var store=new Mock<ISoftwareSecretStore>(MockBehavior.Strict);store.Setup(x=>x.ReadAsync(new(c.Identity,c.Input),ct.Token)).ReturnsAsync(new VaultResult<SoftwareSecretDetails>(c.Details));
        var r=await Handler(store,live,c.Identity.ScopeId).HandleAsync(new(c.Identity,"original-bearer",c.Input),ct.Token);
        Assert.True(r.Success);Assert.Empty(r.Errors);Assert.Equal(new[]{"software_secret_found"},r.Messages);Assert.Equal(c.Details.Target,r.Data!.Target);Assert.Equal(c.Details.Provisioning,r.Data.Provisioning);
        Assert.Equal(c.Details.OwnerAccountId,r.Data.OwnerAccountId);Assert.Equal(c.Details.RecipientKey,r.Data.RecipientKey);Assert.Equal(c.Details.AuthorKey,r.Data.AuthorKey);
        live.VerifyAll();live.VerifyNoOtherCalls();store.VerifyAll();store.VerifyNoOtherCalls();
    }
    [UnitTheory][InlineData("identity","authentication_required")][InlineData("bearer","authentication_required")][InlineData("expired","authentication_required")]
    [InlineData("scope","authentication_required")][InlineData("input","validation_failed")]
    public async Task GivenInvalidTrustedContextOrVisibleInput_WhenGetting_ThenNeverCallProviderOrStore(string kind,string error)
    {
        var c=SoftwareSecretFixture.Create("record",false);var live=new Mock<IHeimdallApplicationClient>(MockBehavior.Strict);var store=new Mock<ISoftwareSecretStore>(MockBehavior.Strict);
        var identity=kind switch{"identity"=>null,"expired"=>c.Identity with{ExpiresAt=DateTimeOffset.FromUnixTimeSeconds(1)},"scope"=>c.Identity with{ScopeId=Guid.NewGuid()},_=>c.Identity};
        var r=await Handler(store,live,c.Identity.ScopeId).HandleAsync(new(identity,kind=="bearer"?null:"original-bearer",kind=="input"?c.Input with{Kind="folder"}:c.Input));
        Assert.Equal(new[]{error},r.Errors);Assert.Null(r.Data);live.VerifyNoOtherCalls();store.VerifyNoOtherCalls();
    }
    [UnitTheory][InlineData("denied")][InlineData("outage")][InlineData("unsafe")][InlineData("null")][InlineData("errorAndData")][InlineData("wrongApplication")][InlineData("wrongCredential")]
    public async Task GivenUnavailableOrMismatchedCurrentApplication_WhenGetting_ThenNeverReachStoreOrReturnIdentity(string kind)
    {
        var c=SoftwareSecretFixture.Create("record",false);var live=new Mock<IHeimdallApplicationClient>(MockBehavior.Strict);var store=new Mock<ISoftwareSecretStore>(MockBehavior.Strict);
        var answer=kind switch
        {
            "denied"=>new HeimdallApplicationAuthentication(Error:"authentication_required"),"outage"=>new(Error:"identity_unavailable"),"unsafe"=>new(Error:"private-provider-error"),
            "null"=>new(),"errorAndData"=>new(c.Identity,"authentication_required"),"wrongApplication"=>new(c.Identity with{ApplicationId=Guid.NewGuid()}),_=>new(c.Identity with{CredentialId=Guid.NewGuid()})
        };
        live.Setup(x=>x.AuthenticateAsync("original-bearer",It.IsAny<CancellationToken>())).ReturnsAsync(answer);
        var r=await Handler(store,live,c.Identity.ScopeId).HandleAsync(new(c.Identity,"original-bearer",c.Input));
        Assert.Equal(new[]{kind=="denied"?"authentication_required":"identity_unavailable"},r.Errors);Assert.Null(r.Data);store.VerifyNoOtherCalls();
    }
    [UnitTheory][InlineData("not_found")][InlineData("authentication_required")][InlineData("persistence_unavailable")][InlineData("private-storage-error")][InlineData("dataAndError")][InlineData("wrongTarget")][InlineData("null")]
    public async Task GivenDeniedOrIncoherentStoreResult_WhenGetting_ThenReturnSafeFailureWithoutPartialData(string kind)
    {
        var c=SoftwareSecretFixture.Create("record",false);var live=new Mock<IHeimdallApplicationClient>(MockBehavior.Strict);live.Setup(x=>x.AuthenticateAsync("original-bearer",It.IsAny<CancellationToken>())).ReturnsAsync(new HeimdallApplicationAuthentication(c.Identity));
        var store=new Mock<ISoftwareSecretStore>(MockBehavior.Strict);
        VaultResult<SoftwareSecretDetails> answer=kind switch{"null"=>new(),"dataAndError"=>new(c.Details,"not_found"),"wrongTarget"=>new(c.Details with{Target=c.Details.Target with{Id=Guid.NewGuid()}}),_=>new(Error:kind)};
        store.Setup(x=>x.ReadAsync(new(c.Identity,c.Input),It.IsAny<CancellationToken>())).ReturnsAsync(answer);
        var r=await Handler(store,live,c.Identity.ScopeId).HandleAsync(new(c.Identity,"original-bearer",c.Input));
        Assert.Equal(new[]{kind is "not_found" or "authentication_required"?kind:"persistence_unavailable"},r.Errors);Assert.Null(r.Data);Assert.Empty(r.Messages);
    }
    [UnitTheory][InlineData(false)][InlineData(true)]
    public async Task GivenCallerCancellationInRequiredDependency_WhenGetting_ThenPropagateWithoutPartialOutput(bool data)
    {
        var c=SoftwareSecretFixture.Create("record",false);using var ct=new CancellationTokenSource();var live=new Mock<IHeimdallApplicationClient>(MockBehavior.Strict);var store=new Mock<ISoftwareSecretStore>(MockBehavior.Strict);
        if(data){live.Setup(x=>x.AuthenticateAsync("original-bearer",ct.Token)).ReturnsAsync(new HeimdallApplicationAuthentication(c.Identity));store.Setup(x=>x.ReadAsync(new(c.Identity,c.Input),ct.Token)).Returns(()=>{ct.Cancel();throw new OperationCanceledException(ct.Token);});}
        else live.Setup(x=>x.AuthenticateAsync("original-bearer",ct.Token)).Returns(()=>{ct.Cancel();throw new OperationCanceledException(ct.Token);});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(store,live,c.Identity.ScopeId).HandleAsync(new(c.Identity,"original-bearer",c.Input),ct.Token));
    }
    [UnitFact]
    public async Task GivenTokenExpiresDuringLiveCheck_WhenGetting_ThenDenyWithoutReadingCiphertext()
    {
        var c=SoftwareSecretFixture.Create("record",false);var clock=new MutableClock{Now=c.Identity.ExpiresAt.AddSeconds(-1)};
        var live=new Mock<IHeimdallApplicationClient>(MockBehavior.Strict);var store=new Mock<ISoftwareSecretStore>(MockBehavior.Strict);
        live.Setup(x=>x.AuthenticateAsync("original-bearer",It.IsAny<CancellationToken>())).Returns(()=>{clock.Now=c.Identity.ExpiresAt;return Task.FromResult(new HeimdallApplicationAuthentication(c.Identity));});
        var handler=new GetSoftwareSecretHandler(store.Object,live.Object,new CerberusOptions{HeimdallScopeId=c.Identity.ScopeId},clock);
        var r=await handler.HandleAsync(new(c.Identity,"original-bearer",c.Input));
        Assert.Equal(new[]{"authentication_required"},r.Errors);Assert.Null(r.Data);store.VerifyNoOtherCalls();
    }
    [UnitTheory][InlineData(false)][InlineData(true)]
    public async Task GivenDependencyTimeoutWithoutCallerCancellation_WhenGetting_ThenReturnSafeUnavailable(bool data)
    {
        var c=SoftwareSecretFixture.Create("record",false);var live=new Mock<IHeimdallApplicationClient>(MockBehavior.Strict);var store=new Mock<ISoftwareSecretStore>(MockBehavior.Strict);
        if(data){live.Setup(x=>x.AuthenticateAsync("original-bearer",It.IsAny<CancellationToken>())).ReturnsAsync(new HeimdallApplicationAuthentication(c.Identity));store.Setup(x=>x.ReadAsync(new(c.Identity,c.Input),It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());}
        else live.Setup(x=>x.AuthenticateAsync("original-bearer",It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        var r=await Handler(store,live,c.Identity.ScopeId).HandleAsync(new(c.Identity,"original-bearer",c.Input));
        Assert.Equal(new[]{data?"persistence_unavailable":"identity_unavailable"},r.Errors);Assert.Null(r.Data);
    }
    private sealed class MutableClock:TimeProvider{internal DateTimeOffset Now{get;set;}public override DateTimeOffset GetUtcNow()=>Now;}
    private static GetSoftwareSecretHandler Handler(Mock<ISoftwareSecretStore> store,Mock<IHeimdallApplicationClient> live,Guid scope)=>new(store.Object,live.Object,new CerberusOptions{HeimdallScopeId=scope},TimeProvider.System);
}
