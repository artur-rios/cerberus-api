from pathlib import Path
r=Path('/tmp/cerberus-uc18-worktree');src=(r/'tests/Application/ArturRios.Cerberus.Query.Tests/ListRecordsHandlerTests.cs').read_text()
start=src.index('    [UnitTheory][InlineData("nullBytes")');end=src.index('    [UnitTheory][InlineData("boundaryChanged")');native=src[start:end].replace('WhenListing_ThenRejectEveryItemWithoutPartialPayload','WhenGetting_ThenRejectWholePayload').replace('MockStore(new(new([Row(1), row], 2, false)))','MockStore(new(new(row,[],null,[])))').replace('new(Actor, Access, null, null)','new(Actor, Access, row.RecordId)')
text='''using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Query.Records;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Query.Tests;

public class GetRecordHandlerTests
{
    private const string Access="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Verifier="66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925";
    private static readonly Guid Actor=Guid.NewGuid();
    [UnitTheory][InlineData("actor","authentication_required")][InlineData("missing","vault_access_required")][InlineData("bad","validation_failed")][InlineData("empty","validation_failed")][InlineData("padded","validation_failed")][InlineData("target","validation_failed")]
    public async Task GivenInvalidContext_WhenGetting_ThenRejectBeforePersistence(string kind,string error)
    {
        var store=new Mock<IRecordReadStore>(MockBehavior.Strict);var r=await Handler(store).HandleAsync(new(kind=="actor"?Guid.Empty:Actor,kind switch{"missing"=>null,"bad"=>"bad","empty"=>"","padded"=>Access+"=",_=>Access},kind=="target"?Guid.Empty:Guid.NewGuid()));
        Assert.Contains(error,r.Errors);Assert.Null(r.Data);store.VerifyNoOtherCalls();
    }
    [UnitTheory][InlineData(false)][InlineData(true)]
    public async Task GivenVisibleDetails_WhenGetting_ThenForwardHashAndCallerTokenAndReturnOnlyEightPublicFields(bool references)
    {
        var row=Row(1);Guid[] profiles=references?[Guid.NewGuid(),Guid.NewGuid()]:[];Guid[] collections=references?[row.RecordId]:[];Guid? folder=references?row.RecordId:null;
        using var ct=new CancellationTokenSource();var store=new Mock<IRecordReadStore>(MockBehavior.Strict);store.Setup(x=>x.ReadAsync(new(Actor,Verifier,row.RecordId),ct.Token)).ReturnsAsync(new VaultResult<RecordReadDetails>(new(row,profiles,folder,collections)));
        var result=await Handler(store).HandleAsync(new(Actor,Access,row.RecordId),ct.Token);Assert.True(result.Success);Assert.Contains("record_found",result.Messages);
        var item=result.Data!;Assert.Equal(row.RecordId,item.RecordId);Assert.Equal(row.Revision,item.Revision);Assert.Equal(row.ServerSequence,item.ServerSequence);Assert.Equal(row.EditedAt,item.EditedAt);
        Assert.Equal(JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope,ProtectionFixture.Json),item.Envelope);Assert.Equal(profiles.Order(),item.ProfileIds);Assert.Equal(collections.Order(),item.CollectionIds);Assert.Equal(folder,item.FolderId);
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(item,ProtectionFixture.Json));Assert.Equal(new[]{"collectionIds","editedAt","envelope","folderId","profileIds","recordId","revision","serverSequence"},json.RootElement.EnumerateObject().Select(x=>x.Name).Order());store.VerifyAll();
    }
    [UnitTheory][InlineData("nullRecord")][InlineData("targetMismatch")][InlineData("zeroId")][InlineData("revisionZero")][InlineData("revisionNegative")][InlineData("revisionUnsafe")][InlineData("sequenceZero")][InlineData("sequenceNegative")][InlineData("sequenceUnsafe")][InlineData("defaultTime")][InlineData("infinityTime")][InlineData("offset")][InlineData("precision")][InlineData("nullProfiles")][InlineData("nullCollections")][InlineData("zeroProfile")][InlineData("duplicateProfile")][InlineData("zeroCollection")][InlineData("duplicateCollection")][InlineData("zeroFolder")]
    public async Task GivenCorruptStoredDetails_WhenGetting_Then503WithoutAnyPartialData(string kind)
    {
        var good=Row(1);var row=kind switch{
            "nullRecord"=>null!,"targetMismatch"=>good with{RecordId=Guid.NewGuid()},"zeroId"=>good with{RecordId=Guid.Empty},
            "revisionZero"=>good with{Revision=0},"revisionNegative"=>good with{Revision=-1},"revisionUnsafe"=>good with{Revision=ProtocolBinary.MaxInteger+1},
            "sequenceZero"=>good with{ServerSequence=0},"sequenceNegative"=>good with{ServerSequence=-1},"sequenceUnsafe"=>good with{ServerSequence=ProtocolBinary.MaxInteger+1},
            "defaultTime"=>good with{EditedAt=default},"infinityTime"=>good with{EditedAt=new DateTimeOffset(5,TimeSpan.Zero)},
            "offset"=>good with{EditedAt=good.EditedAt.ToOffset(TimeSpan.FromHours(1))},"precision"=>good with{EditedAt=good.EditedAt.AddTicks(1)},_=>good};
        Guid[] profiles=kind switch{"nullProfiles"=>null!,"zeroProfile"=>[Guid.Empty],"duplicateProfile"=>[Actor,Actor],_=>[]};
        Guid[] collections=kind switch{"nullCollections"=>null!,"zeroCollection"=>[Guid.Empty],"duplicateCollection"=>[Actor,Actor],_=>[]};
        var result=await Handler(MockStore(new(new(row,profiles,kind=="zeroFolder"?Guid.Empty:null,collections)))).HandleAsync(new(Actor,Access,good.RecordId));Assert.Contains("persistence_unavailable",result.Errors);Assert.Null(result.Data);
    }
    [UnitTheory][InlineData("not_found")][InlineData("vault_access_denied")][InlineData("persistence_unavailable")]
    public async Task GivenAllowlistedStoreError_WhenGetting_ThenReturnSafeErrorWithoutPayload(string error)
    {var result=await Handler(MockStore(new(Error:error))).HandleAsync(new(Actor,Access,Guid.NewGuid()));Assert.Contains(error,result.Errors);Assert.Null(result.Data);}
    [UnitTheory][InlineData("nullResult")][InlineData("nullData")][InlineData("unknownError")][InlineData("errorWithData")][InlineData("successAsError")][InlineData("authenticationAsError")]
    public async Task GivenBrokenStoreContract_WhenGetting_ThenFailClosedWithoutLeakingPrivateErrors(string kind)
    {
        var row=Row(1);VaultResult<RecordReadDetails>? data=kind switch{"nullResult"=>null,"nullData"=>new(),"unknownError"=>new(Error:"SELECT private table failed"),"errorWithData"=>new(new(row,[],null,[]),"not_found"),"successAsError"=>new(Error:"record_found"),_=>new(Error:"authentication_required")};
        var r=await Handler(MockStore(data!)).HandleAsync(new(Actor,Access,row.RecordId));Assert.Contains("persistence_unavailable",r.Errors);Assert.Null(r.Data);Assert.DoesNotContain("SELECT private table failed",r.Errors);
    }
    [UnitFact]
    public async Task GivenCancelledCaller_WhenGetting_ThenPropagateBeforeStore()
    {using var ct=new CancellationTokenSource();ct.Cancel();var store=new Mock<IRecordReadStore>(MockBehavior.Strict);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(store).HandleAsync(new(Actor,Access,Guid.NewGuid()),ct.Token));store.VerifyNoOtherCalls();}
    [UnitFact]
    public async Task GivenStoreCancellation_WhenGetting_ThenPropagateSameCallerToken()
    {using var ct=new CancellationTokenSource();var target=Guid.NewGuid();var store=new Mock<IRecordReadStore>(MockBehavior.Strict);store.Setup(x=>x.ReadAsync(new(Actor,Verifier,target),ct.Token)).ThrowsAsync(new OperationCanceledException(ct.Token));var ex=await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(store).HandleAsync(new(Actor,Access,target),ct.Token));Assert.Equal(ct.Token,ex.CancellationToken);store.VerifyAll();}
'''+native+'''
    private static Mock<IRecordReadStore> MockStore(VaultResult<RecordReadDetails> result)
    {var store=new Mock<IRecordReadStore>(MockBehavior.Strict);store.Setup(x=>x.ReadAsync(It.IsAny<RecordReadRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(result);return store;}
    private static GetRecordHandler Handler(Mock<IRecordReadStore> store)=>new(store.Object);
    private static RecordListRow Row(long sequence)=>new(Guid.NewGuid(),1,sequence,DateTimeOffset.FromUnixTimeSeconds(1780000000),JsonSerializer.SerializeToUtf8Bytes(new EncryptedEnvelope("cerberus-content-v1",1,ProtocolBinary.Encode(new byte[32]),ProtocolBinary.Encode(new byte[12]),"AQ",ProtocolBinary.Encode(new byte[16])),ProtectionFixture.Json));
}
'''
(r/'tests/Application/ArturRios.Cerberus.Query.Tests/GetRecordHandlerTests.cs').write_text(text)
