using System.Text.Json;
using ArturRios.Cerberus.Command.Trash;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Trash;
using Moq;
namespace ArturRios.Cerberus.Command.Tests;

public class EmptyTrashHandlerTests
{
    private const string Access="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Verifier="66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925";
    private static readonly Guid Actor=Guid.NewGuid(),Account=Guid.NewGuid();
    private static readonly DateTimeOffset Time=DateTimeOffset.FromUnixTimeSeconds(1780000000);
    [UnitTheory][InlineData("actor","authentication_required")][InlineData("missing","vault_access_required")]
    [InlineData("access","validation_failed")][InlineData("empty","validation_failed")][InlineData("padded","validation_failed")]
    [InlineData("zero","validation_failed")][InlineData("negative","validation_failed")][InlineData("unsafe","validation_failed")]
    public async Task GivenInvalidCurrentContext_WhenEmptying_ThenRejectBeforePersistence(string damage,string error)
    {
        var store=new Mock<ITrashEmptyStore>(MockBehavior.Strict);var command=Command();command.SetContext(damage=="actor"?Guid.Empty:Actor,damage switch{"missing"=>null,"access"=>"bad","empty"=>"","padded"=>Access+"=",_=>Access});command.ExpectedRevision=damage switch{"zero"=>0,"negative"=>-1,"unsafe"=>ProtocolBinary.MaxInteger+1,_=>1};
        var result=await Handler(store).HandleAsync(command);Assert.Contains(error,result.Errors);Assert.Null(result.Data);store.VerifyNoOtherCalls();
    }
    [UnitTheory][InlineData(0L)][InlineData(10L)][InlineData(ProtocolBinary.MaxInteger)]
    public async Task GivenValidPurgeCounts_WhenHandling_ThenExactMetadataOnlyAndHashedHandle(long count)
    {
        using var ct=new CancellationTokenSource();var store=new Mock<ITrashEmptyStore>(MockBehavior.Strict);store.Setup(s=>s.EmptyAsync(new(Actor,Verifier,1),ct.Token)).ReturnsAsync(new VaultResult<TrashEmptyDetails>(Details(count)));
        var r=await Handler(store).HandleAsync(Command(),ct.Token);Assert.True(r.Success);Assert.Contains("trash_emptied",r.Messages);Assert.Equal(Account,r.Data!.AccountId);Assert.Equal(Time,r.Data.PurgedAt);Assert.Equal(count,r.Data.TotalPurged);Assert.Equal(4,r.Data.Counts.Count);Assert.Equal(count,r.Data.Counts.Single(x=>x.Kind=="record").Count);
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(r.Data,new JsonSerializerOptions(JsonSerializerDefaults.Web)));Assert.Equal(new[]{"accountId","counts","purgedAt","totalPurged"},json.RootElement.EnumerateObject().Select(x=>x.Name).Order());Assert.All(json.RootElement.GetProperty("counts").EnumerateArray(),x=>Assert.Equal(new[]{"count","kind"},x.EnumerateObject().Select(p=>p.Name).Order()));store.VerifyAll();
    }
    [UnitTheory][InlineData("not_found")][InlineData("vault_access_denied")][InlineData("revision_conflict")][InlineData("persistence_unavailable")]
    public async Task GivenSafeStoreFailure_WhenEmptying_ThenStableErrorWithoutData(string error)
    {var r=await Handler(Store(new(Error:error))).HandleAsync(Command());Assert.Contains(error,r.Errors);Assert.Null(r.Data);}
    [UnitTheory][InlineData("nullResult")][InlineData("nullData")][InlineData("unknown")][InlineData("successError")][InlineData("authError")][InlineData("withData")]
    public async Task GivenBrokenStoreContract_WhenEmptying_Then503WithoutPrivateDetails(string damage)
    {
        VaultResult<TrashEmptyDetails>? response=damage switch{"nullResult"=>null,"nullData"=>new(),"unknown"=>new(Error:"SELECT private credentials failed"),"successError"=>new(Error:"trash_emptied"),"authError"=>new(Error:"authentication_required"),_=>new(Details(),"not_found")};
        var r=await Handler(Store(response!)).HandleAsync(Command());Assert.Contains("persistence_unavailable",r.Errors);Assert.Null(r.Data);Assert.DoesNotContain("SELECT private credentials failed",r.Errors);
    }
    [UnitTheory][InlineData("account")][InlineData("time")][InlineData("precision")][InlineData("offset")]
    [InlineData("nullCounts")][InlineData("emptyCounts")][InlineData("missingKind")][InlineData("extraKind")][InlineData("duplicateKind")]
    [InlineData("nullCount")][InlineData("unknownKind")][InlineData("caseKind")][InlineData("negativeCount")][InlineData("unsafeCount")]
    [InlineData("negativeTotal")][InlineData("unsafeTotal")][InlineData("mismatch")]
    public async Task GivenInvalidPurgeMetadata_WhenHandling_ThenRejectWholeResult(string damage)
    {
        var d=Details();IReadOnlyList<TrashPurgedCount> counts=damage switch{
            "nullCounts"=>null!,"emptyCounts"=>[],"missingKind"=>d.Counts.Take(3).ToArray(),"extraKind"=>[..d.Counts,new("grant",1)],
            "duplicateKind"=>[d.Counts[0],d.Counts[0],d.Counts[2],d.Counts[3]],"nullCount"=>[null!,..d.Counts.Skip(1)],
            "unknownKind"=>[new("grant",1),..d.Counts.Skip(1)],"caseKind"=>[new("Record",1),..d.Counts.Skip(1)],
            "negativeCount"=>[new("record",-1),..d.Counts.Skip(1)],"unsafeCount"=>[new("record",ProtocolBinary.MaxInteger+1),..d.Counts.Skip(1)],_=>d.Counts};
        d=d with{AccountId=damage=="account"?Guid.Empty:Account,PurgedAt=damage switch{"time"=>default,"precision"=>Time.AddTicks(1),"offset"=>Time.ToOffset(TimeSpan.FromHours(1)),_=>Time},Counts=counts,TotalPurged=damage switch{"negativeTotal"=>-1,"unsafeTotal"=>ProtocolBinary.MaxInteger+1,"mismatch"=>2,_=>1}};
        var r=await Handler(Store(new(d))).HandleAsync(Command());Assert.Contains("persistence_unavailable",r.Errors);Assert.Null(r.Data);
    }
    [UnitTheory][InlineData("caller")][InlineData("store")]
    public async Task GivenCancellation_WhenEmptying_ThenPropagate(string kind)
    {
        using var ct=new CancellationTokenSource();var store=new Mock<ITrashEmptyStore>(MockBehavior.Strict);if(kind=="caller")ct.Cancel();else store.Setup(s=>s.EmptyAsync(new(Actor,Verifier,1),ct.Token)).ThrowsAsync(new OperationCanceledException(ct.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(store).HandleAsync(Command(),ct.Token));
    }
    [UnitTheory][InlineData("{}")][InlineData("{\"expectedRevision\":\"1\"}")][InlineData("{\"expectedRevision\":1,\"actor\":\"secret\"}")]
    [InlineData("{\"expectedRevision\":null}")][InlineData("{\"expectedRevision\":1.0}")][InlineData("{\"expectedRevision\":1,\"expectedRevision\":1}")][InlineData("{\"ExpectedRevision\":1}")]
    public void GivenUntrustedCommandJson_WhenReading_ThenRejectMissingOrUnauthorizedFields(string json)
    {Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<EmptyTrashCommand>(json,new JsonSerializerOptions(JsonSerializerDefaults.Web){AllowDuplicateProperties=false,PropertyNameCaseInsensitive=false}));}
    [UnitFact]
    public void GivenEmptyTrashMessages_WhenMapping_ThenStableSuccessAndAllRequiredFailureStatuses()
    {var x=EmptyTrashMessages.StatusCodes;Assert.Equal(200,x["trash_emptied"]);Assert.Equal(400,x["validation_failed"]);Assert.Equal(401,x["authentication_required"]);Assert.Equal(401,x["vault_access_required"]);Assert.Equal(403,x["vault_access_denied"]);Assert.Equal(404,x["not_found"]);Assert.Equal(409,x["revision_conflict"]);Assert.Equal(503,x["identity_unavailable"]);Assert.Equal(503,x["persistence_unavailable"]);Assert.Equal(9,x.Count);}
    private static EmptyTrashCommand Command(){var c=new EmptyTrashCommand{ExpectedRevision=1};c.SetContext(Actor,Access);return c;}
    private static TrashEmptyDetails Details(long count=1)=>new(Account,Time,[new("record",count),new("folder",0),new("collection",0),new("profile",0)],count);
    private static Mock<ITrashEmptyStore> Store(VaultResult<TrashEmptyDetails> value){var s=new Mock<ITrashEmptyStore>(MockBehavior.Strict);s.Setup(x=>x.EmptyAsync(new(Actor,Verifier,1),It.IsAny<CancellationToken>())).ReturnsAsync(value);return s;}
    private static EmptyTrashHandler Handler(Mock<ITrashEmptyStore> store)=>new(new EmptyTrashValidator(),store.Object);
}
