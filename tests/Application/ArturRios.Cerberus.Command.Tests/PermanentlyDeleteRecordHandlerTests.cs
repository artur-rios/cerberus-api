using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Command.Records;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;
public sealed class PermanentlyDeleteRecordHandlerTests
{
    private const string Access="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly Guid actor=Guid.NewGuid(),target=Guid.NewGuid();
    private static readonly JsonSerializerOptions Json=new(ProtectionFixture.Json){NumberHandling=JsonNumberHandling.Strict};
    [UnitTheory]
    [InlineData("actor","authentication_required")][InlineData("missing","vault_access_required")]
    [InlineData("empty","validation_failed")][InlineData("handle","validation_failed")][InlineData("noncanonical","validation_failed")]
    [InlineData("id","validation_failed")][InlineData("zero","validation_failed")][InlineData("negative","validation_failed")][InlineData("unsafe","validation_failed")]
    public async Task GivenInvalidAuthorityOrRevision_WhenHandling_ThenRejectBeforePersistence(string kind,string error)
    {
        var c=Command();c.SetContext(kind=="actor"?Guid.Empty:actor,kind=="missing"?null:kind=="empty"?"":kind=="handle"?"bad":kind=="noncanonical"?new string('A',42)+"B":Access,kind=="id"?Guid.Empty:target);
        c.ExpectedRevision=kind=="zero"?0:kind=="negative"?-1:kind=="unsafe"?ProtocolBinary.MaxInteger+1:1;
        var r=await Handler(new Mock<IRecordPermanentDeleteStore>(MockBehavior.Strict).Object).HandleAsync(c);
        Assert.False(r.Success);Assert.Null(r.Data);Assert.Contains(error,r.Errors);
    }
    [UnitTheory][InlineData("normal")][InlineData("minimum")][InlineData("pre2000")][InlineData("largestRevision")][InlineData("maximumTime")]
    public async Task GivenValidTerminalMetadata_WhenHandling_ThenForwardTrustedContextAndReturnExactlyTwoFields(string kind)
    {
        var c=Command();if(kind=="largestRevision")c.ExpectedRevision=ProtocolBinary.MaxInteger;
        var time=kind=="minimum"?new DateTimeOffset(10,TimeSpan.Zero):kind=="pre2000"?new DateTimeOffset(1999,12,31,23,59,59,TimeSpan.Zero):kind=="maximumTime"?new DateTimeOffset(DateTimeOffset.MaxValue.Ticks-9,TimeSpan.Zero):DateTimeOffset.UnixEpoch;
        using var ct=new CancellationTokenSource();RecordPermanentDeleteRequest? received=null;CancellationToken token=default;
        var store=new Mock<IRecordPermanentDeleteStore>(MockBehavior.Strict);
        store.Setup(x=>x.DeleteAsync(It.IsAny<RecordPermanentDeleteRequest>(),It.IsAny<CancellationToken>())).Returns<RecordPermanentDeleteRequest,CancellationToken>((r,t)=>{received=r;token=t;return Task.FromResult(new VaultResult<RecordPermanentDeleteDetails>(new(target,time)));});
        var result=await Handler(store.Object).HandleAsync(c,ct.Token);
        Assert.True(result.Success);Assert.Contains("record_permanently_deleted",result.Messages);Assert.Equal(target,result.Data!.RecordId);Assert.Equal(time,result.Data.DeletedAt);
        Assert.Equal(actor,received!.Actor);Assert.Equal(target,received.RecordId);Assert.Equal(c.ExpectedRevision,received.ExpectedRevision);
        Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925",received.AccessVerifier);Assert.Equal(ct.Token,token);
        using var output=JsonDocument.Parse(JsonSerializer.Serialize(result.Data,Json));Assert.Equal(new[]{"deletedAt","recordId"},output.RootElement.EnumerateObject().Select(x=>x.Name).Order());
        using var input=JsonDocument.Parse(JsonSerializer.Serialize(c,Json));Assert.Equal(new[]{"expectedRevision"},input.RootElement.EnumerateObject().Select(x=>x.Name));
    }
    [UnitTheory]
    [InlineData("validation_failed",400)][InlineData("not_found",404)][InlineData("vault_access_denied",403)][InlineData("revision_conflict",409)][InlineData("persistence_unavailable",503)]
    [InlineData("secret_owner_123",503)][InlineData("authentication_required",503)][InlineData("vault_access_required",503)][InlineData("identity_unavailable",503)][InlineData("record_permanently_deleted",503)][InlineData("",503)]
    public async Task GivenStoreFailure_WhenHandling_ThenOnlyAllowExpectedSafeCodes(string error,int status)
    {
        var store=new Mock<IRecordPermanentDeleteStore>();store.Setup(x=>x.DeleteAsync(It.IsAny<RecordPermanentDeleteRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<RecordPermanentDeleteDetails>(Error:error));
        var r=await Handler(store.Object).HandleAsync(Command());var expected=error is "validation_failed" or "not_found" or "vault_access_denied" or "revision_conflict"?error:"persistence_unavailable";
        Assert.False(r.Success);Assert.Null(r.Data);Assert.Contains(expected,r.Errors);Assert.Equal(status,PermanentlyDeleteRecordMessages.StatusCodes[expected]);
    }
    [UnitTheory][InlineData("not_found")][InlineData("validation_failed")][InlineData("vault_access_denied")][InlineData("revision_conflict")][InlineData("persistence_unavailable")][InlineData("secret_owner_123")]
    public async Task GivenStoreErrorAndData_WhenHandling_Then503WithoutPartialPayload(string error)
    {
        var store=new Mock<IRecordPermanentDeleteStore>();store.Setup(x=>x.DeleteAsync(It.IsAny<RecordPermanentDeleteRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<RecordPermanentDeleteDetails>(Details(),error));
        var r=await Handler(store.Object).HandleAsync(Command());Assert.False(r.Success);Assert.Null(r.Data);Assert.Contains("persistence_unavailable",r.Errors);
    }
    [UnitTheory][InlineData("nullResult")][InlineData("nullData")][InlineData("recordId")][InlineData("emptyId")][InlineData("defaultTime")][InlineData("submicroTime")][InlineData("precision")][InlineData("timeOffset")]
    public async Task GivenMalformedStoreSuccess_WhenHandling_Then503WithoutTerminalMetadata(string kind)
    {
        RecordPermanentDeleteDetails? d=Details();d=kind switch{"nullData"=>null,"recordId"=>d with{RecordId=Guid.NewGuid()},"emptyId"=>d with{RecordId=Guid.Empty},"defaultTime"=>d with{DeletedAt=default},"submicroTime"=>d with{DeletedAt=new(9,TimeSpan.Zero)},"precision"=>d with{DeletedAt=d.DeletedAt.AddTicks(1)},"timeOffset"=>d with{DeletedAt=d.DeletedAt.ToOffset(TimeSpan.FromHours(1))},_=>d};
        var store=new Mock<IRecordPermanentDeleteStore>();store.Setup(x=>x.DeleteAsync(It.IsAny<RecordPermanentDeleteRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(kind=="nullResult"?null!:new VaultResult<RecordPermanentDeleteDetails>(d));
        var r=await Handler(store.Object).HandleAsync(Command());Assert.False(r.Success);Assert.Null(r.Data);Assert.Contains("persistence_unavailable",r.Errors);
    }
    [UnitFact]public void GivenOmittedExpectedRevision_WhenParsing_ThenRejectRequiredMember()=>Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<PermanentlyDeleteRecordCommand>("{}",Json));
    [UnitTheory][InlineData("actor")][InlineData("vaultAccess")][InlineData("recordId")][InlineData("ownerId")][InlineData("profileIds")][InlineData("folderId")][InlineData("collectionIds")][InlineData("name")][InlineData("fields")][InlineData("keyWrappers")][InlineData("envelope")][InlineData("editedAt")][InlineData("trashOperationId")][InlineData("deletedAt")][InlineData("purgeAt")][InlineData("revision")][InlineData("serverSequence")]
    public void GivenForgedContextOrAlternateInput_WhenParsing_ThenReject(string field)
    {var body=JsonSerializer.SerializeToNode(Command(),Json)!.AsObject();body[field]=null;Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<PermanentlyDeleteRecordCommand>(body.ToJsonString(),Json));}
    [UnitTheory][InlineData("duplicate")][InlineData("case")][InlineData("numericString")][InlineData("null")][InlineData("fraction")][InlineData("overflow")]
    public void GivenAlternateRevisionJson_WhenParsing_ThenReject(string kind)
    {var body=kind switch{"duplicate"=>"{\"expectedRevision\":1,\"expectedRevision\":1}","case"=>"{\"ExpectedRevision\":1}","numericString"=>"{\"expectedRevision\":\"1\"}","null"=>"{\"expectedRevision\":null}","fraction"=>"{\"expectedRevision\":1.5}",_=>"{\"expectedRevision\":9223372036854775808}"};Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<PermanentlyDeleteRecordCommand>(body,Json));}
    [UnitFact]public async Task GivenCancelledCaller_WhenHandling_ThenPropagateBeforePersistence(){using var ct=new CancellationTokenSource();ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(new Mock<IRecordPermanentDeleteStore>(MockBehavior.Strict).Object).HandleAsync(Command(),ct.Token));}
    [UnitFact]public async Task GivenPersistenceCancellation_WhenHandling_ThenPropagateExactToken(){using var ct=new CancellationTokenSource();var store=new Mock<IRecordPermanentDeleteStore>();store.Setup(x=>x.DeleteAsync(It.IsAny<RecordPermanentDeleteRequest>(),ct.Token)).ThrowsAsync(new OperationCanceledException(ct.Token));var e=await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(store.Object).HandleAsync(Command(),ct.Token));Assert.Equal(ct.Token,e.CancellationToken);}
    [UnitFact]public void GivenPermanentDeletedMessage_WhenMapping_Then200()=>Assert.Equal(200,PermanentlyDeleteRecordMessages.StatusCodes["record_permanently_deleted"]);
    private PermanentlyDeleteRecordCommand Command(){var c=new PermanentlyDeleteRecordCommand{ExpectedRevision=1};c.SetContext(actor,Access,target);return c;}
    private RecordPermanentDeleteDetails Details()=>new(target,DateTimeOffset.UnixEpoch);
    private static PermanentlyDeleteRecordHandler Handler(IRecordPermanentDeleteStore store)=>new(new PermanentlyDeleteRecordValidator(),store);
}
