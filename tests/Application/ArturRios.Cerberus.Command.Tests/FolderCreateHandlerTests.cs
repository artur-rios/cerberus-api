using System.Text.Json;
using System.Text.Json.Nodes;
using ArturRios.Cerberus.Command.Folders;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class FolderCreateHandlerTests
{
    private const string Access="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly Guid actor=Guid.NewGuid();
    private static readonly JsonSerializerOptions Json=new(ProtectionFixture.Json){NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.Strict};
    [UnitTheory][InlineData("actor","authentication_required")][InlineData("missing","vault_access_required")]
    [InlineData("empty","validation_failed")][InlineData("bad","validation_failed")][InlineData("id","validation_failed")]
    [InlineData("envelope","validation_failed")][InlineData("epoch","validation_failed")][InlineData("profiles","validation_failed")]
    [InlineData("duplicates","validation_failed")][InlineData("folder","validation_failed")][InlineData("time","validation_failed")][InlineData("selfParent","validation_failed")]
    public async Task GivenInvalidInputOrAuthority_WhenHandling_ThenRejectWithoutPersistence(string kind,string error)
    {
        var c=Command();c.SetContext(kind=="actor"?Guid.Empty:actor,kind=="missing"?null:kind=="empty"?"":kind=="bad"?"bad":Access);
        if(kind=="id")c.FolderId=Guid.Empty;if(kind=="envelope")c.Envelope=null!;if(kind=="epoch")c.Envelope=c.Envelope with{KeyEpoch=2};
        if(kind=="profiles")c.ProfileIds=null!;if(kind=="duplicates")c.ProfileIds=[actor,actor];if(kind=="folder")c.ParentFolderId=Guid.Empty;if(kind=="time")c.EditedAt=new(9,TimeSpan.Zero);if(kind=="selfParent")c.ParentFolderId=c.FolderId;
        var r=await Handler(new Mock<IFolderCreateStore>(MockBehavior.Strict).Object).HandleAsync(c);Assert.False(r.Success);Assert.Contains(error,r.Errors);Assert.Null(r.Data);
    }
    [UnitTheory][InlineData("minimum")][InlineData("precision")][InlineData("pre2000")][InlineData("maximum")]
    public async Task GivenValidCommand_WhenHandling_ThenSendTrustedHashAndReturnOnlyNormalizedPublicMetadata(string kind)
    {
        var c=Command();c.EditedAt=kind=="maximum"?DateTimeOffset.MaxValue:kind=="minimum"?new(10,TimeSpan.Zero):kind=="pre2000"?new DateTimeOffset(1999,12,31,23,59,59,TimeSpan.Zero).AddTicks(19):DateTimeOffset.UnixEpoch.AddTicks(19);
        FolderCreateRequest? captured=null;CancellationToken received=default;using var source=new CancellationTokenSource();var store=new Mock<IFolderCreateStore>(MockBehavior.Strict);
        store.Setup(x=>x.CreateAsync(It.IsAny<FolderCreateRequest>(),It.IsAny<CancellationToken>())).Returns<FolderCreateRequest,CancellationToken>((r,t)=>{captured=r;received=t;return Task.FromResult(new VaultResult<FolderCreateDetails>(new(c.FolderId,1,7,Normalize(c.EditedAt))));});
        var result=await Handler(store.Object).HandleAsync(c,source.Token);Assert.True(result.Success);Assert.Contains("folder_created",result.Messages);Assert.Equal(c.FolderId,result.Data!.FolderId);Assert.Equal(1,result.Data.Revision);Assert.Equal(7,result.Data.ServerSequence);Assert.Equal(Normalize(c.EditedAt),result.Data.EditedAt);
        Assert.Equal(actor,captured!.Actor);Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925",captured.AccessVerifier);Assert.Equal(c.ToInput(),captured.Input);Assert.Equal(source.Token,received);
        using var output=JsonDocument.Parse(JsonSerializer.Serialize(result.Data,Json));Assert.Equal(new[]{"editedAt","folderId","revision","serverSequence"},output.RootElement.EnumerateObject().Select(x=>x.Name).Order());
        using var wire=JsonDocument.Parse(JsonSerializer.Serialize(c,Json));Assert.Equal(new[]{"editedAt","envelope","folderId","parentFolderId","profileIds"},wire.RootElement.EnumerateObject().Select(x=>x.Name).Order());
    }
    [UnitTheory][InlineData("validation_failed",400)][InlineData("authentication_required",401)][InlineData("vault_access_required",401)]
    [InlineData("vault_access_denied",403)][InlineData("not_found",404)][InlineData("revision_conflict",409)]
    [InlineData("persistence_unavailable",503)][InlineData("identity_unavailable",503)][InlineData("secret_internal_error",503)][InlineData("folder_created",503)][InlineData("",503)]
    public async Task GivenStoreErrorWithData_WhenHandling_ThenAllowlistErrorAndDiscardPayload(string error,int status)
    {
        var c=Command();var store=new Mock<IFolderCreateStore>();store.Setup(x=>x.CreateAsync(It.IsAny<FolderCreateRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<FolderCreateDetails>(new(c.FolderId,1,7,Normalize(c.EditedAt)),error));
        var r=await Handler(store.Object).HandleAsync(c);var expected=error is "secret_internal_error" or "folder_created" or ""?"persistence_unavailable":error;Assert.False(r.Success);Assert.Null(r.Data);Assert.Contains(expected,r.Errors);Assert.Equal(status,FolderCreateMessages.StatusCodes[expected]);
    }
    [UnitTheory][InlineData("nullResult")][InlineData("nullData")][InlineData("id")][InlineData("revision0")][InlineData("revision2")][InlineData("sequence0")][InlineData("unsafe")]
    [InlineData("time")][InlineData("offset")][InlineData("defaultTime")][InlineData("unnormalized")]
    public async Task GivenCorruptStoreOutput_WhenHandling_ThenFailClosed(string kind)
    {
        var c=Command();var data=new FolderCreateDetails(c.FolderId,1,7,Normalize(c.EditedAt));data=kind switch{"nullData"=>null!,"id"=>data with{FolderId=Guid.NewGuid()},"revision0"=>data with{Revision=0},"revision2"=>data with{Revision=2},"sequence0"=>data with{ServerSequence=0},"unsafe"=>data with{ServerSequence=9007199254740992},"time"=>data with{EditedAt=data.EditedAt.AddTicks(10)},"offset"=>data with{EditedAt=data.EditedAt.ToOffset(TimeSpan.FromHours(1))},"defaultTime"=>data with{EditedAt=default},"unnormalized"=>data with{EditedAt=data.EditedAt.AddTicks(1)},_=>data};
        var store=new Mock<IFolderCreateStore>();store.Setup(x=>x.CreateAsync(It.IsAny<FolderCreateRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(kind=="nullResult"?null!:new VaultResult<FolderCreateDetails>(data));var r=await Handler(store.Object).HandleAsync(c);Assert.Contains("persistence_unavailable",r.Errors);Assert.False(r.Success);Assert.Null(r.Data);
    }
    [UnitTheory][InlineData("folderId")][InlineData("envelope")][InlineData("editedAt")][InlineData("profileIds")]
    public void GivenOmittedRequiredBodyMember_WhenParsing_ThenReject(string name)
    {var body=JsonSerializer.SerializeToNode(Command(),Json)!.AsObject();body.Remove(name);Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<CreateFolderCommand>(body.ToJsonString(),Json));}
    [UnitTheory][InlineData("actor")][InlineData("accountId")][InlineData("vaultAccess")][InlineData("name")][InlineData("fields")][InlineData("templateId")][InlineData("collectionIds")][InlineData("unknown")]
    public void GivenUnknownOrForgedBodyMember_WhenParsing_ThenReject(string name)
    {var body=JsonSerializer.SerializeToNode(Command(),Json)!.AsObject();body[name]=null;Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<CreateFolderCommand>(body.ToJsonString(),Json));}
    [UnitTheory][InlineData("duplicate")][InlineData("case")][InlineData("numeric")]
    public void GivenAmbiguousStrictBody_WhenParsing_ThenReject(string kind)
    {var body=JsonSerializer.Serialize(Command(),Json);body=kind=="duplicate"?body.Insert(1,"\"profileIds\":[],"):kind=="case"?body.Replace("profileIds","ProfileIds"):body.Replace("\"keyEpoch\":1","\"keyEpoch\":\"1\"");Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<CreateFolderCommand>(body,Json));}
    [UnitTheory][InlineData(false)][InlineData(true)]
    public void GivenOmittedOrNullOptionalFolder_WhenParsing_ThenPreserveUnfolderedCreation(bool omit)
    {var body=JsonSerializer.SerializeToNode(Command(),Json)!.AsObject();if(omit)body.Remove("parentFolderId");else body["parentFolderId"]=null;var c=JsonSerializer.Deserialize<CreateFolderCommand>(body.ToJsonString(),Json)!;Assert.Null(c.ParentFolderId);Assert.True(c.ToInput().IsValid());}
    [UnitFact]
    public async Task GivenCancelledCaller_WhenHandling_ThenPropagateBeforePersistence()
    {using var source=new CancellationTokenSource();source.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(new Mock<IFolderCreateStore>(MockBehavior.Strict).Object).HandleAsync(Command(),source.Token));}
    [UnitFact]
    public async Task GivenStoreCancellation_WhenHandling_ThenPropagateSameCallerToken()
    {using var source=new CancellationTokenSource();var store=new Mock<IFolderCreateStore>();store.Setup(x=>x.CreateAsync(It.IsAny<FolderCreateRequest>(),source.Token)).ThrowsAsync(new OperationCanceledException(source.Token));var e=await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(store.Object).HandleAsync(Command(),source.Token));Assert.Equal(source.Token,e.CancellationToken);}
    [UnitFact]
    public void GivenFolderCreatedMessage_WhenMapping_ThenReturn201()=>Assert.Equal(201,FolderCreateMessages.StatusCodes["folder_created"]);
    private CreateFolderCommand Command(){var c=new CreateFolderCommand{FolderId=Guid.NewGuid(),Envelope=new EncryptedEnvelope("cerberus-content-v1",1,Access,"AAAAAAAAAAAAAAAA","AQID","AAAAAAAAAAAAAAAAAAAAAA"),EditedAt=DateTimeOffset.UnixEpoch.AddTicks(19),ProfileIds=[],ParentFolderId=null};c.SetContext(actor,Access);return c;}
    private static DateTimeOffset Normalize(DateTimeOffset value)=>value.AddTicks(-(value.Ticks%10));
    private static CreateFolderHandler Handler(IFolderCreateStore store)=>new(new CreateFolderValidator(),store);
}
