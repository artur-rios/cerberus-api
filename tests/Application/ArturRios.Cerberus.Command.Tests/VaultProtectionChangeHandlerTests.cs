using System.Text;
using ArturRios.Cerberus.Command.Protection;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class VaultProtectionChangeHandlerTests
{
    private static readonly Guid Actor=Guid.NewGuid();private static readonly Guid Account=Guid.NewGuid();private static readonly Guid Challenge=Guid.NewGuid();
    private static readonly byte[] Body=Encoding.UTF8.GetBytes("{\"original\":true}");
    private static readonly string Access=ProtectionFixture.Encode(new byte[32]);private static readonly string Proof=ProtectionFixture.Encode(new byte[64]);

    [UnitTheory]
    [InlineData("actor","authentication_required")][InlineData("missingAccess","vault_access_required")][InlineData("access","validation_failed")]
    [InlineData("account","validation_failed")][InlineData("revision","validation_failed")][InlineData("accountRevision","validation_failed")]
    [InlineData("mode","validation_failed")][InlineData("material","validation_failed")][InlineData("content","validation_failed")]
    [InlineData("challenge","validation_failed")][InlineData("proof","validation_failed")][InlineData("raw","validation_failed")]
    public async Task GivenInvalidChangeOrTrustedContext_WhenHandling_ThenRejectBeforePersistence(string invalid,string error)
    {
        using var client=new ProtectionFixture();var c=Command(client);
        if(invalid=="account")c.AccountId=Guid.Empty;if(invalid=="revision")c.ExpectedProtectionRevision=0;if(invalid=="accountRevision")c.ExpectedAccountRevision=9007199254740992;
        if(invalid=="mode")c.Mode="partial";if(invalid=="material")c.Material=null!;if(invalid=="content")c.ContentReplacements=null!;
        c.SetContext(invalid=="actor"?Guid.Empty:Actor,invalid=="missingAccess"?null:invalid=="access"?"bad":Access,
            invalid=="challenge"?Guid.Empty:Challenge,invalid=="proof"?"bad":Proof,invalid=="raw"?[]:Body);
        var result=await new ChangeVaultProtectionHandler(new ChangeVaultProtectionValidator(),Strict()).HandleAsync(c);
        Assert.Contains(error,result.Errors);Assert.Null(result.Data);
    }

    [UnitFact]
    public async Task GivenValidChange_WhenHandling_ThenBindOriginalContextAndReturnOnlyConfirmation()
    {
        using var client=new ProtectionFixture();var c=Command(client);OpaqueAccessHandle.TryHash(Access,out var hash);
        var store=new Mock<IVaultProtectionChangeStore>(MockBehavior.Strict);
        store.Setup(x=>x.ChangeAsync(It.Is<ProtectionChangeRequest>(r=>r.Actor==Actor&&r.AccessVerifier==hash&&r.ChallengeId==Challenge
            &&r.Proof==Proof&&r.RawBody==Body&&r.Change.AccountId==Account&&r.Change.Material==c.Material&&r.Change.ExpectedProtectionRevision==1&&r.Change.ExpectedAccountRevision==3),It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VaultResult<ProtectionChangeDetails>(new(Account,2,2,1,3)));
        var result=await new ChangeVaultProtectionHandler(new ChangeVaultProtectionValidator(),store.Object).HandleAsync(c);
        Assert.True(result.Success);Assert.Equal(Account,result.Data!.AccountId);Assert.Equal(2,result.Data.ProtectionRevision);Assert.Equal(3,result.Data.AccountRevision);
        Assert.Contains("protection_changed",result.Messages);Assert.Equal(200,VaultProtectionMessages.StatusCodes["protection_changed"]);
    }

    [UnitTheory]
    [InlineData("vault_access_denied",403)][InlineData("vault_proof_rejected",401)][InlineData("not_found",404)]
    [InlineData("revision_conflict",409)][InlineData("persistence_unavailable",503)][InlineData("validation_failed",400)]
    public async Task GivenFailedStore_WhenHandling_ThenMapStableErrorWithoutOutput(string error,int status)
    {
        using var client=new ProtectionFixture();var store=new Mock<IVaultProtectionChangeStore>();
        store.Setup(x=>x.ChangeAsync(It.IsAny<ProtectionChangeRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<ProtectionChangeDetails>(Error:error));
        var result=await new ChangeVaultProtectionHandler(new ChangeVaultProtectionValidator(),store.Object).HandleAsync(Command(client));
        Assert.Contains(error,result.Errors);Assert.Null(result.Data);Assert.Equal(status,VaultProtectionMessages.StatusCodes[error]);
    }

    [UnitTheory]
    [InlineData("missing")][InlineData("account")][InlineData("protection")][InlineData("epoch")][InlineData("generation")][InlineData("accountRevision")]
    public async Task GivenIncompleteConfirmation_WhenHandling_ThenFailClosed(string invalid)
    {
        using var client=new ProtectionFixture();var store=new Mock<IVaultProtectionChangeStore>();
        var data=new ProtectionChangeDetails(invalid=="account"?Guid.NewGuid():Account,invalid=="protection"?1:2,invalid=="epoch"?1:2,invalid=="generation"?2:1,invalid=="accountRevision"?4:3);
        store.Setup(x=>x.ChangeAsync(It.IsAny<ProtectionChangeRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<ProtectionChangeDetails>(invalid=="missing"?null:data));
        var result=await new ChangeVaultProtectionHandler(new ChangeVaultProtectionValidator(),store.Object).HandleAsync(Command(client));
        Assert.Contains("persistence_unavailable",result.Errors);Assert.Null(result.Data);
    }

    [UnitTheory]
    [InlineData("change-protection",true)][InlineData("unlock-account",false)]
    public async Task GivenChangeChallengeRequest_WhenHandling_ThenRequireMatchingReturnedPurpose(string returned,bool success)
    {
        var command=new IssueVaultChallengeCommand { Operation="change-protection",RequestHash=ProtectionFixture.Encode(new byte[32]) };command.SetActor(Actor);
        var challenge=new VaultProofChallenge("cerberus-challenge-v1",Challenge,ProtectionFixture.Encode(new byte[32]),returned,Actor,Account,"account",Account,1,1,null,command.RequestHash,1000,1060);
        var store=new Mock<IVaultProtectionStore>(MockBehavior.Strict);store.Setup(x=>x.ChallengeAsync(Actor,"change-protection",command.RequestHash,It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<VaultProofChallenge>(challenge));
        var result=await new IssueVaultChallengeHandler(new IssueVaultChallengeValidator(),store.Object).HandleAsync(command);
        Assert.Equal(success,result.Success);if(success)Assert.Equal(challenge,result.Data!.Challenge);else{Assert.Null(result.Data);Assert.Contains("persistence_unavailable",result.Errors);}
    }

    [UnitFact]
    public async Task GivenCancelledCaller_WhenChanging_ThenPropagateBeforeStore()
    {
        using var client=new ProtectionFixture();using var source=new CancellationTokenSource();source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ChangeVaultProtectionHandler(new ChangeVaultProtectionValidator(),Strict()).HandleAsync(Command(client),source.Token));
    }
    private static IVaultProtectionChangeStore Strict()=>new Mock<IVaultProtectionChangeStore>(MockBehavior.Strict).Object;
    private static ChangeVaultProtectionCommand Command(ProtectionFixture client)
    {
        var c=new ChangeVaultProtectionCommand { AccountId=Account,ExpectedProtectionRevision=1,ExpectedAccountRevision=3,Mode="rewrap",Material=client.Rewrap(),ContentReplacements=[] };
        c.SetContext(Actor,Access,Challenge,Proof,Body);return c;
    }
}
