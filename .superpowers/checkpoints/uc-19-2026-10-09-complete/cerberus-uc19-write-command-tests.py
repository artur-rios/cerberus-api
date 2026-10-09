from pathlib import Path
p=Path('/tmp/cerberus-uc19-worktree/tests/Application/ArturRios.Cerberus.Command.Tests/UpdateRecordHandlerTests.cs')
p.write_text(r'''using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Command.Records;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.TestSupport;
using Moq;
namespace ArturRios.Cerberus.Command.Tests;

public class UpdateRecordHandlerTests
{
    private const string Access="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly Guid actor=Guid.NewGuid();private readonly Guid target=Guid.NewGuid();
    private static readonly JsonSerializerOptions Json=new(ProtectionFixture.Json){NumberHandling=JsonNumberHandling.Strict};
    [UnitTheory][InlineData("actor","authentication_required")][InlineData("missing","vault_access_required")][InlineData("empty","validation_failed")][InlineData("handle","validation_failed")][InlineData("id","validation_failed")][InlineData("revision","validation_failed")][InlineData("unsafeRevision","validation_failed")][InlineData("envelope","validation_failed")][InlineData("format","validation_failed")][InlineData("epoch","validation_failed")][InlineData("salt","validation_failed")][InlineData("nonce","validation_failed")][InlineData("tag","validation_failed")][InlineData("cipher","validation_failed")][InlineData("time","validation_failed")][InlineData("offset","validation_failed")]
    public async Task GivenInvalidAuthorityOrInput_WhenHandling_ThenRejectBeforePersistence(string kind,string error)
    {
        var c=Command();c.SetContext(kind=="actor"?Guid.Empty:actor,kind=="missing"?null:kind=="empty"?"":kind=="handle"?"bad":Access,kind=="id"?Guid.Empty:target);
        if(kind=="revision")c.ExpectedRevision=0;if(kind=="unsafeRevision")c.ExpectedRevision=ProtocolBinary.MaxInteger+1;if(kind=="envelope")c.Envelope=null!;if(kind=="format")c.Envelope=c.Envelope with{Format="unknown"};if(kind=="epoch")c.Envelope=c.Envelope with{KeyEpoch=0};if(kind=="salt")c.Envelope=c.Envelope with{KeySalt="bad"};if(kind=="nonce")c.Envelope=c.Envelope with{Nonce="bad"};if(kind=="tag")c.Envelope=c.Envelope with{Tag="bad"};if(kind=="cipher")c.Envelope=c.Envelope with{Ciphertext="AQID="};if(kind=="time")c.EditedAt=new(9,TimeSpan.Zero);if(kind=="offset")c.EditedAt=c.EditedAt.ToOffset(TimeSpan.FromHours(1));
        var result=await Handler(new Mock<IRecordUpdateStore>(MockBehavior.Strict).Object).HandleAsync(c);Assert.False(result.Success);Assert.Null(result.Data);Assert.Contains(error,result.Errors);
    }
    [UnitTheory][InlineData("minimum")][InlineData("pre2000")][InlineData("precision")][InlineData("laterEpoch")]
    public async Task GivenValidReplacement_WhenHandling_ThenForwardTrustedContextHashAndReturnExactFourMetadataFields(string kind)
    {
        var c=Command();c.EditedAt=kind=="minimum"?new(10,TimeSpan.Zero):kind=="pre2000"?new DateTimeOffset(1999,12,31,23,59,59,TimeSpan.Zero).AddTicks(19):DateTimeOffset.UnixEpoch.AddTicks(19);if(kind=="laterEpoch")c.Envelope=c.Envelope with{KeyEpoch=7};
        using var ct=new CancellationTokenSource();RecordUpdateRequest? received=null;CancellationToken token=default;var store=new Mock<IRecordUpdateStore>(MockBehavior.Strict);store.Setup(x=>x.UpdateAsync(It.IsAny<RecordUpdateRequest>(),It.IsAny<CancellationToken>())).Returns<RecordUpdateRequest,CancellationToken>((r,t)=>{received=r;token=t;return Task.FromResult(new VaultResult<RecordCreateDetails>(new(target,2,7,Normalize(c.EditedAt))));});
        var result=await Handler(store.Object).HandleAsync(c,ct.Token);Assert.True(result.Success);Assert.Contains("record_updated",result.Messages);Assert.Equal(target,result.Data!.RecordId);Assert.Equal(2,result.Data.Revision);Assert.Equal(7,result.Data.ServerSequence);Assert.Equal(Normalize(c.EditedAt),result.Data.EditedAt);Assert.Equal(actor,received!.Actor);Assert.Equal(target,received.RecordId);Assert.Equal(c.ToInput(),received.Input);Assert.Equal(ct.Token,token);Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925",received.AccessVerifier);
        using var data=JsonDocument.Parse(JsonSerializer.Serialize(result.Data,Json));Assert.Equal(new[]{"editedAt","recordId","revision","serverSequence"},data.RootElement.EnumerateObject().Select(x=>x.Name).Order());using var wire=JsonDocument.Parse(JsonSerializer.Serialize(c,Json));Assert.Equal(new[]{"editedAt","envelope","expectedRevision"},wire.RootElement.EnumerateObject().Select(x=>x.Name).Order());
    }
    [UnitTheory][InlineData("validation_failed",400)][InlineData("not_found",404)][InlineData("vault_access_denied",403)][InlineData("revision_conflict",409)][InlineData("persistence_unavailable",503)]
    [InlineData("secret_owner_123",503)][InlineData("authentication_required",503)][InlineData("vault_access_required",503)][InlineData("identity_unavailable",503)][InlineData("record_updated",503)][InlineData("",503)]
    public async Task GivenStoreError_WhenHandling_ThenAllowlistOnlyExpectedPersistenceFailures(string error,int status)
    {var store=new Mock<IRecordUpdateStore>();store.Setup(x=>x.UpdateAsync(It.IsAny<RecordUpdateRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<RecordCreateDetails>(Error:error));var r=await Handler(store.Object).HandleAsync(Command());var expected=error is "validation_failed" or "not_found" or "vault_access_denied" or "revision_conflict"?error:"persistence_unavailable";Assert.False(r.Success);Assert.Null(r.Data);Assert.Contains(expected,r.Errors);Assert.Equal(status,RecordUpdateMessages.StatusCodes[expected]);}
    [UnitTheory][InlineData("not_found")][InlineData("validation_failed")][InlineData("vault_access_denied")][InlineData("revision_conflict")][InlineData("persistence_unavailable")][InlineData("secret_owner_123")]
    public async Task GivenStoreErrorAndData_WhenHandling_ThenBrokenContract503WithoutPayload(string error)
    {var c=Command();var store=new Mock<IRecordUpdateStore>();store.Setup(x=>x.UpdateAsync(It.IsAny<RecordUpdateRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<RecordCreateDetails>(new(target,2,7,Normalize(c.EditedAt)),error));var r=await Handler(store.Object).HandleAsync(c);Assert.False(r.Success);Assert.Null(r.Data);Assert.Contains("persistence_unavailable",r.Errors);}
    [UnitTheory][InlineData("nullResult")][InlineData("nullData")][InlineData("id")][InlineData("revision0")][InlineData("revisionOld")][InlineData("revisionAhead")][InlineData("unsafeRevision")][InlineData("sequence0")][InlineData("negativeSequence")][InlineData("unsafeSequence")][InlineData("time")][InlineData("offset")][InlineData("defaultTime")][InlineData("precision")]
    public async Task GivenMalformedStoreSuccess_WhenHandling_Then503WithoutPartialMetadata(string kind)
    {var c=Command();RecordCreateDetails? d=new(target,2,7,Normalize(c.EditedAt));d=kind switch{"nullData"=>null,"id"=>d with{RecordId=Guid.NewGuid()},"revision0"=>d with{Revision=0},"revisionOld"=>d with{Revision=1},"revisionAhead"=>d with{Revision=3},"unsafeRevision"=>d with{Revision=ProtocolBinary.MaxInteger+1},"sequence0"=>d with{ServerSequence=0},"negativeSequence"=>d with{ServerSequence=-1},"unsafeSequence"=>d with{ServerSequence=ProtocolBinary.MaxInteger+1},"time"=>d with{EditedAt=d.EditedAt.AddTicks(10)},"offset"=>d with{EditedAt=d.EditedAt.ToOffset(TimeSpan.FromHours(1))},"defaultTime"=>d with{EditedAt=default},"precision"=>d with{EditedAt=d.EditedAt.AddTicks(1)},_=>d};var store=new Mock<IRecordUpdateStore>();store.Setup(x=>x.UpdateAsync(It.IsAny<RecordUpdateRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(kind=="nullResult"?null!:new VaultResult<RecordCreateDetails>(d));var r=await Handler(store.Object).HandleAsync(c);Assert.False(r.Success);Assert.Null(r.Data);Assert.Contains("persistence_unavailable",r.Errors);}
    [UnitTheory][InlineData("expectedRevision")][InlineData("envelope")][InlineData("editedAt")]
    public void GivenOmittedRequiredMember_WhenParsing_ThenReject(string name)
    {var body=JsonSerializer.SerializeToNode(Command(),Json)!.AsObject();body.Remove(name);Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<UpdateRecordCommand>(body.ToJsonString(),Json));}
    [UnitTheory][InlineData("actor")][InlineData("vaultAccess")][InlineData("recordId")][InlineData("ownerId")][InlineData("profileIds")][InlineData("folderId")][InlineData("collectionIds")][InlineData("name")][InlineData("fields")][InlineData("keyWrappers")]
    public void GivenForgedContextOrRelationshipMember_WhenParsing_ThenReject(string name)
    {var body=JsonSerializer.SerializeToNode(Command(),Json)!.AsObject();body[name]=null;Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<UpdateRecordCommand>(body.ToJsonString(),Json));}
    [UnitTheory][InlineData("duplicate")][InlineData("case")][InlineData("numericRevision")][InlineData("numericEpoch")]
    public void GivenAlternateJsonShape_WhenParsing_ThenReject(string kind)
    {var body=JsonSerializer.Serialize(Command(),Json);body=kind switch{"duplicate"=>body.Insert(1,"\"expectedRevision\":1,"),"case"=>body.Replace("expectedRevision","ExpectedRevision"),"numericRevision"=>body.Replace("\"expectedRevision\":1","\"expectedRevision\":\"1\""),_=>body.Replace("\"keyEpoch\":1","\"keyEpoch\":\"1\"")};Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<UpdateRecordCommand>(body,Json));}
    [UnitFact]public async Task GivenCancelledCaller_WhenHandling_ThenPropagateBeforePersistence(){using var ct=new CancellationTokenSource();ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(new Mock<IRecordUpdateStore>(MockBehavior.Strict).Object).HandleAsync(Command(),ct.Token));}
    [UnitFact]public async Task GivenStoreCancellation_WhenHandling_ThenPropagateExactCallerToken(){using var ct=new CancellationTokenSource();var store=new Mock<IRecordUpdateStore>();store.Setup(x=>x.UpdateAsync(It.IsAny<RecordUpdateRequest>(),ct.Token)).ThrowsAsync(new OperationCanceledException(ct.Token));var e=await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(store.Object).HandleAsync(Command(),ct.Token));Assert.Equal(ct.Token,e.CancellationToken);}
    [UnitFact]public void GivenUpdatedMessage_WhenMapping_Then200()=>Assert.Equal(200,RecordUpdateMessages.StatusCodes["record_updated"]);
    private UpdateRecordCommand Command(){var c=new UpdateRecordCommand{ExpectedRevision=1,Envelope=new("cerberus-content-v1",1,Access,"AAAAAAAAAAAAAAAA","AQID","AAAAAAAAAAAAAAAAAAAAAA"),EditedAt=DateTimeOffset.UnixEpoch.AddTicks(19)};c.SetContext(actor,Access,target);return c;}
    private static DateTimeOffset Normalize(DateTimeOffset time)=>time.AddTicks(-(time.Ticks%10));
    private static UpdateRecordHandler Handler(IRecordUpdateStore store)=>new(new UpdateRecordValidator(),store);
}
''')
print('UC19 command failing behavior matrix written before product.')
