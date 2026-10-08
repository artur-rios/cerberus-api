using System.Text.Json;
using ArturRios.Cerberus.Command.Protection;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class VaultRecoveryHandlerTests
{
    private static readonly Guid Actor=Guid.NewGuid(),Challenge=Guid.NewGuid();
    private static readonly long Issued=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static readonly string Proof=ProtectionFixture.Encode(new byte[64]);
    [UnitTheory]
    [InlineData("actor","authentication_required")][InlineData("operation","validation_failed")][InlineData("key","validation_failed")]
    [InlineData("revision","validation_failed")][InlineData("password","validation_failed")][InlineData("recovery","validation_failed")]
    [InlineData("verifier","validation_failed")][InlineData("challenge","validation_failed")][InlineData("proof","validation_failed")]
    [InlineData("raw","validation_failed")][InlineData("iat","validation_failed")]
    public async Task GivenInvalidRecoveryOrTrustedContext_WhenHandling_ThenRejectBeforeStore(string invalid,string error)
    {
        using var client=new ProtectionFixture();var c=Command(client);
        if(invalid=="operation")c.Operation="unlock-account";if(invalid=="key")c.IdempotencyKey=Guid.Empty;if(invalid=="revision")c.ExpectedRevision=9007199254740992;
        if(invalid=="password")c.PasswordWrapper=null!;if(invalid=="recovery")c.RecoveryWrapper=null!;if(invalid=="verifier")c.NewRecoveryVerifier=null!;
        c.SetContext(invalid=="actor"?Guid.Empty:Actor,invalid=="iat"?-1:Issued,invalid=="challenge"?Guid.Empty:Challenge,invalid=="proof"?"bad":Proof,invalid=="raw"?[]:[1]);
        var result=await Handler(Strict()).HandleAsync(c);Assert.Contains(error,result.Errors);Assert.Null(result.Data);
    }
    [UnitFact]
    public async Task GivenValidRecovery_WhenHandling_ThenBindTrustedContextAndReturnFourNonsecretFields()
    {
        using var client=new ProtectionFixture();var c=Command(client);var raw=JsonSerializer.SerializeToUtf8Bytes(c,ProtectionFixture.Json);c.SetContext(Actor,Issued,Challenge,Proof,raw);
        var store=new Mock<IVaultRecoveryStore>(MockBehavior.Strict);
        store.Setup(x=>x.RecoverAsync(It.Is<RecoveryRequest>(r=>r.Actor==Actor&&r.IdentityIssuedAt==Issued&&r.ChallengeId==Challenge&&r.Proof==Proof&&r.RawBody==raw&&r.Replacement==c.ToReplacement()),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<RecoveryDetails>(new("committed",2,2,7)));
        var result=await Handler(store.Object).HandleAsync(c);Assert.True(result.Success);Assert.Equal(7,result.Data!.RevocationGeneration);Assert.Equal(2,result.Data.Generation);Assert.Equal(2,result.Data.ProtectionRevision);Assert.Equal("committed",result.Data.Status);
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(result.Data,ProtectionFixture.Json));Assert.Equal(new[]{"status","protectionRevision","generation","revocationGeneration"},json.RootElement.EnumerateObject().Select(x=>x.Name));
        Assert.Contains("recovery_committed",result.Messages);Assert.Equal(200,VaultProtectionMessages.StatusCodes["recovery_committed"]);
    }
    [UnitTheory]
    [InlineData("missing")][InlineData("status")][InlineData("revision")][InlineData("generation")][InlineData("revocation")][InlineData("overflow")]
    public async Task GivenInvalidDurableResult_WhenHandling_ThenFailClosed(string invalid)
    {
        using var client=new ProtectionFixture();var store=new Mock<IVaultRecoveryStore>();var data=new RecoveryDetails(invalid=="status"?"pending":"committed",invalid=="revision"?1:2,invalid=="generation"?3:2,invalid=="revocation"?0:invalid=="overflow"?9007199254740992:2);
        store.Setup(x=>x.RecoverAsync(It.IsAny<RecoveryRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<RecoveryDetails>(invalid=="missing"?null:data));
        var result=await Handler(store.Object).HandleAsync(Command(client));Assert.Contains("persistence_unavailable",result.Errors);Assert.Null(result.Data);
    }
    [UnitTheory]
    [InlineData("not_found",404)][InlineData("vault_proof_rejected",401)][InlineData("fresh_authentication_required",401)]
    [InlineData("recovery_credential_consumed",409)][InlineData("revision_conflict",409)][InlineData("validation_failed",400)][InlineData("persistence_unavailable",503)]
    public async Task GivenFailedRecovery_WhenHandling_ThenMapStableError(string error,int status)
    {
        using var client=new ProtectionFixture();var store=new Mock<IVaultRecoveryStore>();store.Setup(x=>x.RecoverAsync(It.IsAny<RecoveryRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<RecoveryDetails>(Error:error));
        var result=await Handler(store.Object).HandleAsync(Command(client));Assert.Contains(error,result.Errors);Assert.Null(result.Data);Assert.Equal(status,VaultProtectionMessages.StatusCodes[error]);
    }
    [UnitTheory]
    [InlineData(1L,true)][InlineData(null,false)][InlineData(0L,false)][InlineData(9007199254740992L,false)]
    public async Task GivenRecoveryChallenge_WhenHandling_ThenRequireCurrentSafeGeneration(long? generation,bool success)
    {
        var c=new IssueVaultChallengeCommand { Operation="recover",RequestHash=ProtectionFixture.Encode(new byte[32]) };c.SetActor(Actor);var id=Guid.NewGuid();
        var challenge=new VaultProofChallenge("cerberus-challenge-v1",Challenge,ProtectionFixture.Encode(new byte[32]),"recover",Actor,id,"account",id,1,1,generation,c.RequestHash,1000,1060);
        var store=new Mock<IVaultProtectionStore>(MockBehavior.Strict);store.Setup(x=>x.ChallengeAsync(Actor,"recover",c.RequestHash,It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<VaultProofChallenge>(challenge));
        var result=await new IssueVaultChallengeHandler(new IssueVaultChallengeValidator(),store.Object).HandleAsync(c);Assert.Equal(success,result.Success);if(success)Assert.Equal(challenge,result.Data!.Challenge);else Assert.Contains("persistence_unavailable",result.Errors);
    }
    [UnitFact]
    public async Task GivenCancelledCaller_WhenRecovering_ThenPropagateBeforeStore()
    {using var client=new ProtectionFixture();using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(Strict()).HandleAsync(Command(client),cancel.Token));}
    private static IVaultRecoveryStore Strict()=>new Mock<IVaultRecoveryStore>(MockBehavior.Strict).Object;
    private static RecoverVaultHandler Handler(IVaultRecoveryStore store)=>new(new RecoverVaultValidator(),store);
    private static RecoverVaultCommand Command(ProtectionFixture client)
    {
        var m=client.Rewrap();var c=new RecoverVaultCommand { Operation="recover",IdempotencyKey=Guid.NewGuid(),ExpectedRevision=1,PasswordWrapper=m.PasswordWrapper,RecoveryWrapper=m.RecoveryWrapper with { Generation=2 },NewRecoveryVerifier=m.RecoveryVerifier };
        c.SetContext(Actor,Issued,Challenge,Proof,[1]);return c;
    }
}
