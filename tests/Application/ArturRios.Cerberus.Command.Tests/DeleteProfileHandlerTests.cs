using System.Text.Json;
using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using Moq;
namespace ArturRios.Cerberus.Command.Tests;
public class DeleteProfileHandlerTests
{
    private const string Access="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly Guid actor=Guid.NewGuid(),id=Guid.NewGuid();
    private static readonly DateTimeOffset Time=DateTimeOffset.Parse("2026-10-08T12:00:00Z");
    [UnitTheory][InlineData("actor","authentication_required")][InlineData("missing","vault_access_required")][InlineData("empty","validation_failed")][InlineData("bad","validation_failed")][InlineData("id","validation_failed")][InlineData("revision","validation_failed")][InlineData("negative","validation_failed")][InlineData("unsafe","validation_failed")]
    public async Task GivenInvalidCommandOrAccess_WhenDeleting_ThenRejectBeforeStore(string kind,string error)
    {
        var c=Command();c.SetContext(kind=="actor"?Guid.Empty:actor,kind=="missing"?null:kind=="empty"?"":kind=="bad"?"bad":Access,kind=="id"?Guid.Empty:id);if(kind=="revision")c.ExpectedRevision=0;if(kind=="negative")c.ExpectedRevision=-1;if(kind=="unsafe")c.ExpectedRevision=9007199254740992;var r=await Handler(new Mock<IProfileTrashStore>(MockBehavior.Strict).Object).HandleAsync(c);Assert.Contains(error,r.Errors);Assert.False(r.Success);Assert.Null(r.Data);
    }
    [UnitFact]
    public async Task GivenValidDelete_WhenHandling_ThenBindTrustedRequestHashHandleAndReturnSixMetadataFields()
    {
        var c=Command();ProfileTrashRequest? captured=null;var d=Details();var store=new Mock<IProfileTrashStore>(MockBehavior.Strict);store.Setup(x=>x.TrashAsync(It.IsAny<ProfileTrashRequest>(),It.IsAny<CancellationToken>())).Returns<ProfileTrashRequest,CancellationToken>((r,_)=>{captured=r;return Task.FromResult(new VaultResult<ProfileTrashDetails>(d));});var result=await Handler(store.Object).HandleAsync(c);Assert.True(result.Success);Assert.Contains("profile_deleted",result.Messages);Assert.Equal(id,result.Data!.ProfileId);Assert.Equal(d.TrashOperationId,result.Data.TrashOperationId);Assert.Equal(2,result.Data.Revision);Assert.Equal(7,result.Data.ServerSequence);Assert.Equal(Time,result.Data.DeletedAt);Assert.Equal(Time.AddDays(30),result.Data.PurgeAt);Assert.Equal(actor,captured!.Actor);Assert.Equal(id,captured.ProfileId);Assert.Equal(1,captured.ExpectedRevision);Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925",captured.AccessVerifier);Assert.Single(JsonDocument.Parse(JsonSerializer.Serialize(c)).RootElement.EnumerateObject());Assert.Equal(6,JsonDocument.Parse(JsonSerializer.Serialize(result.Data)).RootElement.EnumerateObject().Count());
    }
    [UnitTheory][InlineData("not_found")][InlineData("vault_access_denied")][InlineData("revision_conflict")][InlineData("validation_failed")][InlineData("persistence_unavailable")][InlineData("authentication_required")]
    public async Task GivenStoreFailure_WhenHandling_ThenStableErrorWithoutData(string error)
    {var store=new Mock<IProfileTrashStore>();store.Setup(x=>x.TrashAsync(It.IsAny<ProfileTrashRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<ProfileTrashDetails>(Error:error));var r=await Handler(store.Object).HandleAsync(Command());Assert.Contains(error,r.Errors);Assert.Null(r.Data);}
    [UnitTheory][InlineData("null")][InlineData("id")][InlineData("operation")][InlineData("revision")][InlineData("unsafeRevision")][InlineData("sequence")][InlineData("unsafeSequence")][InlineData("time")][InlineData("offset")][InlineData("ticks")][InlineData("deadline")][InlineData("deadlineOffset")][InlineData("deadlineTicks")]
    public async Task GivenCorruptStoreMetadata_WhenHandling_Then503WithoutData(string kind)
    {var d=Details();d=kind switch {"null"=>null!,"id"=>d with {ProfileId=Guid.NewGuid()},"operation"=>d with {TrashOperationId=Guid.Empty},"revision"=>d with {Revision=1},"unsafeRevision"=>d with {Revision=9007199254740992},"sequence"=>d with {ServerSequence=0},"unsafeSequence"=>d with {ServerSequence=9007199254740992},"time"=>d with {DeletedAt=default,PurgeAt=default(DateTimeOffset).AddDays(30)},"offset"=>d with {DeletedAt=Time.ToOffset(TimeSpan.FromHours(1))},"ticks"=>d with {DeletedAt=Time.AddTicks(1),PurgeAt=Time.AddDays(30).AddTicks(1)},"deadline"=>d with {PurgeAt=Time.AddDays(29)},"deadlineOffset"=>d with {PurgeAt=Time.AddDays(30).ToOffset(TimeSpan.FromHours(1))},_=>d with {PurgeAt=Time.AddDays(30).AddTicks(1)}};var store=new Mock<IProfileTrashStore>();store.Setup(x=>x.TrashAsync(It.IsAny<ProfileTrashRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<ProfileTrashDetails>(d));var r=await Handler(store.Object).HandleAsync(Command());Assert.Contains("persistence_unavailable",r.Errors);Assert.Null(r.Data);}
    [UnitFact]
    public async Task GivenCancelledCaller_WhenDeleting_ThenPropagateWithoutStore()
    {using var c=new CancellationTokenSource();c.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(new Mock<IProfileTrashStore>(MockBehavior.Strict).Object).HandleAsync(Command(),c.Token));}
    [UnitTheory][InlineData("profile_deleted",200)][InlineData("validation_failed",400)][InlineData("vault_access_required",401)][InlineData("authentication_required",401)][InlineData("vault_access_denied",403)][InlineData("not_found",404)][InlineData("revision_conflict",409)][InlineData("persistence_unavailable",503)][InlineData("identity_unavailable",503)]
    public void GivenMessage_WhenMappingHttp_ThenStableStatus(string message,int status)=>Assert.Equal(status,DeleteProfileMessages.StatusCodes[message]);
    private ProfileTrashDetails Details()=>new(id,Guid.NewGuid(),2,7,Time,Time.AddDays(30));
    private DeleteProfileCommand Command(){var c=new DeleteProfileCommand{ExpectedRevision=1};c.SetContext(actor,Access,id);return c;}
    private static DeleteProfileHandler Handler(IProfileTrashStore store)=>new(new DeleteProfileValidator(),store);
}
