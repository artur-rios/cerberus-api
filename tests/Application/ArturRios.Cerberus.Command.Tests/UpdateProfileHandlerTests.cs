using System.Text.Json;
using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using Moq;
namespace ArturRios.Cerberus.Command.Tests;
public class UpdateProfileHandlerTests
{
    private const string Access="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly Guid actor=Guid.NewGuid(),id=Guid.NewGuid();
    [UnitTheory][InlineData("actor","authentication_required")][InlineData("missing","vault_access_required")][InlineData("empty","validation_failed")][InlineData("bad","validation_failed")][InlineData("id","validation_failed")][InlineData("envelope","validation_failed")][InlineData("revision","validation_failed")][InlineData("unsafe","validation_failed")][InlineData("time","validation_failed")][InlineData("offset","validation_failed")][InlineData("format","validation_failed")]
    public async Task GivenInvalidCommandOrAccess_WhenUpdating_ThenRejectBeforeStore(string kind,string error)
    {
        var c=Command();c.SetContext(kind=="actor"?Guid.Empty:actor,kind=="missing"?null:kind=="empty"?"":kind=="bad"?"bad":Access,kind=="id"?Guid.Empty:id);if(kind=="envelope")c.Envelope=null!;if(kind=="revision")c.ExpectedRevision=0;if(kind=="unsafe")c.ExpectedRevision=9007199254740992;if(kind=="time")c.EditedAt=default;if(kind=="offset")c.EditedAt=c.EditedAt.ToOffset(TimeSpan.FromHours(1));if(kind=="format")c.Envelope=c.Envelope with {Format="unknown"};var r=await Handler(new Mock<IProfileUpdateStore>(MockBehavior.Strict).Object).HandleAsync(c);Assert.Contains(error,r.Errors);Assert.False(r.Success);Assert.Null(r.Data);
    }
    [UnitTheory][InlineData("2026-10-08T12:00:00Z","2026-10-08T12:00:00Z")][InlineData("1999-12-31T23:59:59.9999999Z","1999-12-31T23:59:59.9999990Z")][InlineData("2000-01-01T00:00:00.0000009Z","2000-01-01T00:00:00Z")]
    public async Task GivenValidUpdate_WhenHandling_ThenBindTrustedRequestAndReturnNormalizedMetadata(string submitted,string expected)
    {
        var c=Command();c.EditedAt=DateTimeOffset.Parse(submitted);ProfileUpdateRequest? captured=null;var store=new Mock<IProfileUpdateStore>(MockBehavior.Strict);store.Setup(x=>x.UpdateAsync(It.IsAny<ProfileUpdateRequest>(),It.IsAny<CancellationToken>())).Returns<ProfileUpdateRequest,CancellationToken>((r,_)=>{captured=r;return Task.FromResult(new VaultResult<ProfileCreateDetails>(new(id,2,7,DateTimeOffset.Parse(expected))));});var result=await Handler(store.Object).HandleAsync(c);Assert.True(result.Success);Assert.Contains("profile_updated",result.Messages);Assert.Equal(id,result.Data!.ProfileId);Assert.Equal(2,result.Data.Revision);Assert.Equal(7,result.Data.ServerSequence);Assert.Equal(DateTimeOffset.Parse(expected),result.Data.EditedAt);Assert.Equal(actor,captured!.Actor);Assert.Equal(id,captured.ProfileId);Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925",captured.AccessVerifier);Assert.Equal(c.ToInput(),captured.Input);var json=JsonSerializer.Serialize(c);Assert.DoesNotContain("Actor",json);Assert.DoesNotContain("VaultAccess",json);Assert.DoesNotContain("ProfileId",json);Assert.DoesNotContain("Ciphertext",JsonSerializer.Serialize(result.Data));
    }
    [UnitTheory][InlineData("not_found")][InlineData("vault_access_denied")][InlineData("revision_conflict")][InlineData("validation_failed")][InlineData("persistence_unavailable")]
    public async Task GivenStoreFailure_WhenHandling_ThenStableErrorWithoutData(string error)
    {var store=new Mock<IProfileUpdateStore>();store.Setup(x=>x.UpdateAsync(It.IsAny<ProfileUpdateRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<ProfileCreateDetails>(Error:error));var r=await Handler(store.Object).HandleAsync(Command());Assert.Contains(error,r.Errors);Assert.Null(r.Data);}
    [UnitTheory][InlineData("null")][InlineData("id")][InlineData("revision")][InlineData("sequence")][InlineData("unsafe")][InlineData("time")][InlineData("offset")][InlineData("ticks")]
    public async Task GivenCorruptStoreMetadata_WhenHandling_Then503WithoutData(string kind)
    {var c=Command();var d=new ProfileCreateDetails(id,2,7,c.EditedAt);d=kind switch {"null"=>null!,"id"=>d with {ProfileId=Guid.NewGuid()},"revision"=>d with {Revision=1},"sequence"=>d with {ServerSequence=0},"unsafe"=>d with {ServerSequence=9007199254740992},"time"=>d with {EditedAt=c.EditedAt.AddMinutes(1)},"offset"=>d with {EditedAt=c.EditedAt.ToOffset(TimeSpan.FromHours(1))},_=>d with {EditedAt=c.EditedAt.AddTicks(1)}};var store=new Mock<IProfileUpdateStore>();store.Setup(x=>x.UpdateAsync(It.IsAny<ProfileUpdateRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<ProfileCreateDetails>(d));var r=await Handler(store.Object).HandleAsync(c);Assert.Contains("persistence_unavailable",r.Errors);Assert.Null(r.Data);}
    [UnitFact]
    public async Task GivenCancelledCaller_WhenUpdating_ThenPropagateWithoutStore()
    {using var c=new CancellationTokenSource();c.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(new Mock<IProfileUpdateStore>(MockBehavior.Strict).Object).HandleAsync(Command(),c.Token));}
    private UpdateProfileCommand Command(){var c=new UpdateProfileCommand{ExpectedRevision=1,Envelope=new("cerberus-content-v1",1,Access,"AAAAAAAAAAAAAAAA","AQID","AAAAAAAAAAAAAAAAAAAAAA"),EditedAt=DateTimeOffset.Parse("2026-10-08T12:00:00Z")};c.SetContext(actor,Access,id);return c;}
    private static UpdateProfileHandler Handler(IProfileUpdateStore store)=>new(new UpdateProfileValidator(),store);
}
