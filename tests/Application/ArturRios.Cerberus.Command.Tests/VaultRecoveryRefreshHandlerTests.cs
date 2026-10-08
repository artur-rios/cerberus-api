using ArturRios.Cerberus.Command.Protection;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class VaultRecoveryRefreshHandlerTests
{
    private static readonly Guid Actor=Guid.NewGuid(),Challenge=Guid.NewGuid();
    private static readonly string Access=ProtectionFixture.Encode(new byte[32]),Proof=ProtectionFixture.Encode(new byte[64]);
    [UnitFact]
    public async Task GivenCurrentRefreshContext_WhenHandling_ThenHashAccessAndBindOriginalPurpose()
    {
        using var client=new ProtectionFixture();var c=Command(client);OpaqueAccessHandle.TryHash(Access,out var verifier);var store=new Mock<IVaultRecoveryStore>(MockBehavior.Strict);
        store.Setup(x=>x.RecoverAsync(It.Is<RecoveryRequest>(r=>r.Actor==Actor&&r.AccessVerifier==verifier&&r.Replacement.Operation=="refresh-recovery"&&r.Replacement==c.ToReplacement()&&r.ChallengeId==Challenge&&r.Proof==Proof&&r.RawBody.Length==1),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<RecoveryDetails>(new("committed",2,2,2)));
        var result=await new RecoverVaultHandler(new RecoverVaultValidator(),store.Object).HandleAsync(c);Assert.True(result.Success);Assert.Equal("committed",result.Data!.Status);Assert.Equal(2,result.Data.Generation);
    }
    [UnitTheory]
    [InlineData(null,"vault_access_required")][InlineData("bad","validation_failed")][InlineData("","validation_failed")]
    public async Task GivenMissingOrMalformedRefreshAccess_WhenHandling_ThenRejectBeforeStore(string? access,string error)
    {
        using var client=new ProtectionFixture();var c=Command(client);c.SetContext(Actor,1000,Challenge,Proof,[1],access);
        var result=await new RecoverVaultHandler(new RecoverVaultValidator(),new Mock<IVaultRecoveryStore>(MockBehavior.Strict).Object).HandleAsync(c);Assert.Contains(error,result.Errors);Assert.Null(result.Data);
    }
    [UnitFact]
    public async Task GivenRecoverPurposeWithExtraneousAccess_WhenHandling_ThenNeverUseHandleForRecoveryAuthorization()
    {
        using var client=new ProtectionFixture();var c=Command(client);c.Operation="recover";c.SetContext(Actor,1000,Challenge,Proof,[1],"untrusted");var store=new Mock<IVaultRecoveryStore>(MockBehavior.Strict);
        store.Setup(x=>x.RecoverAsync(It.Is<RecoveryRequest>(r=>r.AccessVerifier==null&&r.Replacement.Operation=="recover"),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<RecoveryDetails>(new("committed",2,2,2)));
        Assert.True((await new RecoverVaultHandler(new RecoverVaultValidator(),store.Object).HandleAsync(c)).Success);
    }
    [UnitTheory]
    [InlineData("vault_access_denied",403)][InlineData("revision_conflict",409)][InlineData("vault_proof_rejected",401)][InlineData("persistence_unavailable",503)]
    public async Task GivenRefreshStoreFailure_WhenHandling_ThenReturnStableErrorWithoutOutcome(string error,int status)
    {
        using var client=new ProtectionFixture();var store=new Mock<IVaultRecoveryStore>();store.Setup(x=>x.RecoverAsync(It.IsAny<RecoveryRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<RecoveryDetails>(Error:error));
        var result=await new RecoverVaultHandler(new RecoverVaultValidator(),store.Object).HandleAsync(Command(client));Assert.Contains(error,result.Errors);Assert.Null(result.Data);Assert.Equal(status,VaultProtectionMessages.StatusCodes[error]);
    }
    [UnitTheory]
    [InlineData(null,true)][InlineData(1L,false)]
    public async Task GivenRefreshChallenge_WhenHandling_ThenRequireNullRecoveryGeneration(long? generation,bool success)
    {
        var c=new IssueVaultChallengeCommand { Operation="refresh-recovery",RequestHash=ProtectionFixture.Encode(new byte[32]) };c.SetActor(Actor);var account=Guid.NewGuid();
        var challenge=new VaultProofChallenge("cerberus-challenge-v1",Challenge,ProtectionFixture.Encode(new byte[32]),"refresh-recovery",Actor,account,"account",account,1,1,generation,c.RequestHash,1000,1060);
        var store=new Mock<IVaultProtectionStore>(MockBehavior.Strict);store.Setup(x=>x.ChallengeAsync(Actor,"refresh-recovery",c.RequestHash,It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<VaultProofChallenge>(challenge));
        var result=await new IssueVaultChallengeHandler(new IssueVaultChallengeValidator(),store.Object).HandleAsync(c);Assert.Equal(success,result.Success);if(!success)Assert.Contains("persistence_unavailable",result.Errors);
    }
    private static RecoverVaultCommand Command(ProtectionFixture client)
    {
        var m=client.Rewrap();var c=new RecoverVaultCommand { Operation="refresh-recovery",IdempotencyKey=Guid.NewGuid(),ExpectedRevision=1,PasswordWrapper=m.PasswordWrapper,RecoveryWrapper=m.RecoveryWrapper with { Generation=2 },NewRecoveryVerifier=m.RecoveryVerifier };
        c.SetContext(Actor,1000,Challenge,Proof,[1],Access);return c;
    }
}
