using System.Text.Json;
using ArturRios.Cerberus.Command.Trash;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Trash;
using Moq;
namespace ArturRios.Cerberus.Command.Tests;

public class RestoreTrashHandlerTests
{
    private const string Access="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Verifier="66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925";
    private static readonly Guid Actor=Guid.NewGuid(),Operation=Guid.NewGuid(),Root=Guid.NewGuid();
    private static readonly DateTimeOffset Time=DateTimeOffset.FromUnixTimeSeconds(1780000000);
    [UnitTheory][InlineData("actor","authentication_required")][InlineData("missing","vault_access_required")]
    [InlineData("access","validation_failed")][InlineData("empty","validation_failed")][InlineData("padded","validation_failed")]
    [InlineData("id","validation_failed")][InlineData("zero","validation_failed")][InlineData("negative","validation_failed")][InlineData("unsafe","validation_failed")]
    public async Task GivenInvalidContext_WhenRestoring_ThenRejectBeforePersistence(string damage,string error)
    {
        var store=new Mock<ITrashRestoreStore>(MockBehavior.Strict);var command=Command();
        command.SetContext(damage=="actor"?Guid.Empty:Actor,damage switch{"missing"=>null,"access"=>"bad","empty"=>"","padded"=>Access+"=",_=>Access},damage=="id"?Guid.Empty:Operation);
        command.ExpectedRevision=damage switch{"zero"=>0,"negative"=>-1,"unsafe"=>ProtocolBinary.MaxInteger+1,_=>1};
        var r=await Handler(store).HandleAsync(command);Assert.Contains(error,r.Errors);Assert.Null(r.Data);store.VerifyNoOtherCalls();
    }
    [UnitTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenValidRestoredRoot_WhenHandling_ThenExactMetadataWithoutCiphertextOrHiddenAssociations(string kind)
    {
        var details=Details(kind);using var ct=new CancellationTokenSource();var store=new Mock<ITrashRestoreStore>(MockBehavior.Strict);
        store.Setup(x=>x.RestoreAsync(new(Actor,Verifier,Operation,1),ct.Token)).ReturnsAsync(new VaultResult<TrashRestoreDetails>(details));
        var result=await Handler(store).HandleAsync(Command(),ct.Token);Assert.True(result.Success);Assert.Contains("trash_restored",result.Messages);
        Assert.Equal(Operation,result.Data!.OperationId);Assert.Equal(Root,result.Data.RootResourceId);Assert.Equal(kind,result.Data.RootKind);Assert.Equal(Time,result.Data.RestoredAt);
        var row=Assert.Single(result.Data.Items);Assert.Equal(2,row.Revision);Assert.Equal(7,row.ServerSequence);Assert.Equal(Time,row.EditedAt);
        var omission=Assert.Single(result.Data.Omissions);Assert.Equal("parent",omission.AssociationKind);Assert.Equal(1,omission.Count);
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(result.Data,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(new[]{"items","omissions","operationId","restoredAt","rootKind","rootResourceId"},json.RootElement.EnumerateObject().Select(x=>x.Name).Order());
        Assert.Equal(new[]{"editedAt","kind","resourceId","revision","serverSequence"},json.RootElement.GetProperty("items")[0].EnumerateObject().Select(x=>x.Name).Order());
        Assert.Equal(new[]{"associationKind","count","resourceId","resourceKind"},json.RootElement.GetProperty("omissions")[0].EnumerateObject().Select(x=>x.Name).Order());store.VerifyAll();
    }
    [UnitTheory][InlineData("not_found")][InlineData("vault_access_denied")][InlineData("revision_conflict")][InlineData("trash_expired")][InlineData("persistence_unavailable")]
    public async Task GivenSafePersistenceError_WhenRestoring_ThenNoPayload(string error)
    {var r=await Handler(Store(new(Error:error))).HandleAsync(Command());Assert.Contains(error,r.Errors);Assert.Null(r.Data);}
    [UnitTheory][InlineData("nullResult")][InlineData("nullData")][InlineData("unknown")][InlineData("successError")][InlineData("authError")][InlineData("withData")]
    public async Task GivenBrokenStoreContract_WhenRestoring_ThenStable503WithoutPrivateDetails(string damage)
    {
        VaultResult<TrashRestoreDetails>? response=damage switch{"nullResult"=>null,"nullData"=>new(),"unknown"=>new(Error:"SELECT private credentials failed"),"successError"=>new(Error:"trash_restored"),"authError"=>new(Error:"authentication_required"),_=>new(Details(),"not_found")};
        var r=await Handler(Store(response!)).HandleAsync(Command());Assert.Contains("persistence_unavailable",r.Errors);Assert.Null(r.Data);Assert.DoesNotContain("SELECT private credentials failed",r.Errors);
    }
    [UnitTheory][InlineData("operation")][InlineData("kind")][InlineData("root")][InlineData("restoredTime")][InlineData("restoredPrecision")]
    [InlineData("nullItems")][InlineData("emptyItems")][InlineData("nullItem")][InlineData("missingRoot")][InlineData("wrongKind")]
    [InlineData("id")][InlineData("revision")][InlineData("unsafeRevision")][InlineData("rootRevision")][InlineData("sequence")][InlineData("unsafeSequence")]
    [InlineData("editedTime")][InlineData("editedPrecision")][InlineData("offset")][InlineData("duplicate")][InlineData("duplicateSequence")]
    [InlineData("nullOmissions")][InlineData("nullOmission")][InlineData("omissionKind")][InlineData("associationKind")]
    [InlineData("omissionId")][InlineData("omissionCount")][InlineData("duplicateOmission")]
    public async Task GivenInvalidRestorationMetadata_WhenHandling_ThenRejectWholeResult(string damage)
    {
        var d=Details();var row=d.Items[0];row=damage switch{
            "wrongKind"=>row with{Kind="grant"},"id"=>row with{ResourceId=Guid.Empty},"revision"=>row with{Revision=0},
            "unsafeRevision"=>row with{Revision=ProtocolBinary.MaxInteger+1},"rootRevision"=>row with{Revision=3},
            "sequence"=>row with{ServerSequence=0},"unsafeSequence"=>row with{ServerSequence=ProtocolBinary.MaxInteger+1},
            "editedTime"=>row with{EditedAt=default},"editedPrecision"=>row with{EditedAt=Time.AddTicks(1)},"offset"=>row with{EditedAt=Time.ToOffset(TimeSpan.FromHours(1))},_=>row};
        IReadOnlyList<TrashRestoredItem> items=damage switch{"nullItems"=>null!,"emptyItems"=>[],"nullItem"=>[null!],"missingRoot"=>[row with{ResourceId=Guid.NewGuid()}],"duplicate"=>[row,row with{ServerSequence=8}],"duplicateSequence"=>[row,row with{ResourceId=Guid.NewGuid()}],_=>[row]};
        var omission=d.Omissions[0];omission=damage switch{"omissionKind"=>omission with{ResourceKind="grant"},"associationKind"=>omission with{AssociationKind="privateIds"},"omissionId"=>omission with{ResourceId=Guid.NewGuid()},"omissionCount"=>omission with{Count=0},_=>omission};
        d=d with{OperationId=damage=="operation"?Guid.NewGuid():Operation,RootKind=damage=="kind"?"grant":"record",RootResourceId=damage=="root"?Guid.Empty:Root,
            RestoredAt=damage=="restoredTime"?default:damage=="restoredPrecision"?Time.AddTicks(1):Time,Items=items,
            Omissions=damage=="nullOmissions"?null!:damage=="nullOmission"?[null!]:damage=="duplicateOmission"?[omission,omission]:[omission]};
        var r=await Handler(Store(new(d))).HandleAsync(Command());Assert.Contains("persistence_unavailable",r.Errors);Assert.Null(r.Data);
    }
    [UnitTheory][InlineData("caller")][InlineData("store")]
    public async Task GivenCancellation_WhenRestoring_ThenPropagate(string kind)
    {
        using var ct=new CancellationTokenSource();var store=new Mock<ITrashRestoreStore>(MockBehavior.Strict);
        if(kind=="caller")ct.Cancel();else store.Setup(x=>x.RestoreAsync(new(Actor,Verifier,Operation,1),ct.Token)).ThrowsAsync(new OperationCanceledException(ct.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(store).HandleAsync(Command(),ct.Token));
    }
    [UnitTheory][InlineData("{}")][InlineData("{\"expectedRevision\":\"1\"}")][InlineData("{\"expectedRevision\":1,\"actor\":\"secret\"}")]
    public void GivenUntrustedCommandJson_WhenDeserializing_ThenRejectMissingOrUnauthorizedFields(string json)
    {Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<RestoreTrashCommand>(json,new JsonSerializerOptions(JsonSerializerDefaults.Web)));}
    [UnitTheory][InlineData(false)][InlineData(true)]
    public async Task GivenFolderUnitWithTypedSameGuid_WhenHandling_ThenAllowOnlyDistinctSequences(bool duplicateSequence)
    {
        var d=Details("folder") with{Items=[new("folder",Root,2,7,Time),new("record",Root,3,duplicateSequence?7:8,Time)]};
        var result=await Handler(Store(new(d))).HandleAsync(Command());
        if(duplicateSequence){Assert.Contains("persistence_unavailable",result.Errors);Assert.Null(result.Data);}
        else{Assert.True(result.Success);Assert.Equal(2,result.Data!.Items.Count);}
    }
    [UnitTheory][InlineData("profile")][InlineData("collection")][InlineData("record")]
    public async Task GivenNoncascadeRootWithUnrelatedRestoredMember_WhenHandling_ThenRejectBrokenUnit(string kind)
    {
        var d=Details(kind) with{Items=[new(kind,Root,2,7,Time),new("record",Guid.NewGuid(),2,8,Time)]};
        var r=await Handler(Store(new(d))).HandleAsync(Command());Assert.Contains("persistence_unavailable",r.Errors);Assert.Null(r.Data);
    }
    private static RestoreTrashCommand Command(){var c=new RestoreTrashCommand{ExpectedRevision=1};c.SetContext(Actor,Access,Operation);return c;}
    private static TrashRestoreDetails Details(string kind="record")=>new(Operation,kind,Root,Time,[new(kind,Root,2,7,Time)],[new(kind,Root,"parent",1)]);
    private static Mock<ITrashRestoreStore> Store(VaultResult<TrashRestoreDetails> result){var s=new Mock<ITrashRestoreStore>(MockBehavior.Strict);s.Setup(x=>x.RestoreAsync(new(Actor,Verifier,Operation,1),It.IsAny<CancellationToken>())).ReturnsAsync(result);return s;}
    private static RestoreTrashHandler Handler(Mock<ITrashRestoreStore> store)=>new(new RestoreTrashValidator(),store.Object);
}
