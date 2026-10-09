using System.Text.Json;
using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class ProfileCreateHandlerTests
{
    private const string Access="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly Guid actor=Guid.NewGuid(),account=Guid.NewGuid();
    [UnitTheory]
    [InlineData("actor","authentication_required")][InlineData("missing","vault_access_required")]
    [InlineData("empty","validation_failed")][InlineData("bad","validation_failed")][InlineData("id","validation_failed")]
    [InlineData("envelope","validation_failed")][InlineData("wrapper","validation_failed")][InlineData("relationships","validation_failed")]
    public async Task GivenInvalidCommandOrAccess_WhenCreating_ThenRejectWithoutPersistence(string invalid,string error)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var c=Command(client,scoped);
        c.SetContext(invalid=="actor"?Guid.Empty:actor,invalid=="missing"?null:invalid=="empty"?"":invalid=="bad"?"bad":Access);
        if(invalid=="id")c.ProfileId=Guid.Empty;if(invalid=="envelope")c.Envelope=null!;if(invalid=="wrapper")c.KeyWrappers=null!;if(invalid=="relationships")c.RecordIds=null!;
        var result=await Handler(new Mock<IProfileCreateStore>(MockBehavior.Strict).Object).HandleAsync(c);
        Assert.Contains(error,result.Errors);Assert.False(result.Success);Assert.Null(result.Data);
    }
    [UnitFact]
    public async Task GivenValidCommand_WhenHandling_ThenBindTrustedActorHashedHandleAndOnlyReturnMetadata()
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var c=Command(client,scoped);ProfileCreateRequest? captured=null;var store=new Mock<IProfileCreateStore>(MockBehavior.Strict);
        store.Setup(x=>x.CreateAsync(It.IsAny<ProfileCreateRequest>(),It.IsAny<CancellationToken>())).Returns<ProfileCreateRequest,CancellationToken>((r,_)=>{captured=r;return Task.FromResult(new VaultResult<ProfileCreateDetails>(new(c.ProfileId,1,7,c.EditedAt)));});
        var result=await Handler(store.Object).HandleAsync(c);Assert.True(result.Success);Assert.Contains("profile_created",result.Messages);Assert.Equal(c.ProfileId,result.Data!.ProfileId);Assert.Equal(7,result.Data.ServerSequence);
        Assert.Equal(actor,captured!.Actor);Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925",captured.AccessVerifier);
        Assert.Equal(c.ToInput(),captured.Input);var json=JsonSerializer.Serialize(c);Assert.DoesNotContain("VaultAccess",json);Assert.DoesNotContain("Actor",json);
        Assert.DoesNotContain("Ciphertext",JsonSerializer.Serialize(result.Data));
    }
    [UnitTheory][InlineData("not_found")][InlineData("vault_access_denied")][InlineData("revision_conflict")][InlineData("persistence_unavailable")][InlineData("validation_failed")]
    public async Task GivenStoreFailure_WhenHandling_ThenReturnStableErrorWithoutData(string error)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var store=new Mock<IProfileCreateStore>();
        store.Setup(x=>x.CreateAsync(It.IsAny<ProfileCreateRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<ProfileCreateDetails>(Error:error));
        var result=await Handler(store.Object).HandleAsync(Command(client,scoped));Assert.Contains(error,result.Errors);Assert.Null(result.Data);
    }
    [UnitTheory][InlineData("null")][InlineData("id")][InlineData("revision")][InlineData("sequence")][InlineData("unsafe")][InlineData("time")]
    public async Task GivenIncompleteOrSubstitutedStoreResult_WhenHandling_ThenFailClosed(string invalid)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var c=Command(client,scoped);var data=new ProfileCreateDetails(c.ProfileId,1,2,c.EditedAt);
        data=invalid switch {"null"=>null!,"id"=>data with {ProfileId=Guid.NewGuid()},"revision"=>data with {Revision=2},"sequence"=>data with {ServerSequence=0},"unsafe"=>data with {ServerSequence=9007199254740992},_=>data with {EditedAt=c.EditedAt.AddMinutes(1)}};
        var store=new Mock<IProfileCreateStore>();store.Setup(x=>x.CreateAsync(It.IsAny<ProfileCreateRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<ProfileCreateDetails>(data));
        var result=await Handler(store.Object).HandleAsync(c);Assert.Contains("persistence_unavailable",result.Errors);Assert.Null(result.Data);
    }
    [UnitFact]
    public async Task GivenCancelledCaller_WhenHandling_ThenPropagateWithoutStore()
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();using var cts=new CancellationTokenSource();cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(new Mock<IProfileCreateStore>(MockBehavior.Strict).Object).HandleAsync(Command(client,scoped),cts.Token));
    }
    private CreateProfileCommand Command(ProtectionFixture client,ProtectionFixture scoped)
    {
        var id=Guid.NewGuid();var c=new CreateProfileCommand {ProfileId=id,Envelope=new("cerberus-content-v1",1,Access,"AAAAAAAAAAAAAAAA","AQID","AAAAAAAAAAAAAAAAAAAAAA"),
            KeyWrappers=new("Master",scoped.Material.UnlockVerifier,client.Wrap(account,"profile",id,actor),null),EditedAt=DateTimeOffset.UtcNow,RecordIds=[],FolderIds=[],CollectionIds=[]};c.SetContext(actor,Access);return c;
    }
    private static CreateProfileHandler Handler(IProfileCreateStore store)=>new(new CreateProfileValidator(),store);
}
