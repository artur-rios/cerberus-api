using System.Text.Json;
using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using Moq;
namespace ArturRios.Cerberus.Command.Tests;
public class SetProfileAssociationsHandlerTests
{
    private const string Access="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly Guid actor=Guid.NewGuid(),id=Guid.NewGuid();
    [UnitTheory][InlineData("actor","authentication_required")][InlineData("missing","vault_access_required")][InlineData("empty","validation_failed")][InlineData("bad","validation_failed")][InlineData("id","validation_failed")][InlineData("revision","validation_failed")][InlineData("unsafe","validation_failed")][InlineData("nullRecords","validation_failed")][InlineData("nullFolders","validation_failed")][InlineData("nullCollections","validation_failed")][InlineData("zeroRecord","validation_failed")][InlineData("duplicateFolder","validation_failed")][InlineData("duplicateCollection","validation_failed")]
    public async Task GivenInvalidContextOrTypedSets_WhenHandling_ThenRejectBeforeStore(string kind,string error)
    {
        var c=Command();c.SetContext(kind=="actor"?Guid.Empty:actor,kind=="missing"?null:kind=="empty"?"":kind=="bad"?"bad":Access,kind=="id"?Guid.Empty:id);
        if(kind=="revision")c.ExpectedRevision=0;if(kind=="unsafe")c.ExpectedRevision=ProtocolBinary.MaxInteger+1;if(kind=="nullRecords")c.RecordIds=null!;if(kind=="nullFolders")c.FolderIds=null!;if(kind=="nullCollections")c.CollectionIds=null!;if(kind=="zeroRecord")c.RecordIds=[Guid.Empty];if(kind=="duplicateFolder")c.FolderIds=[id,id];if(kind=="duplicateCollection")c.CollectionIds=[id,id];
        var r=await Handler(new Mock<IProfileAssociationStore>(MockBehavior.Strict).Object).HandleAsync(c);Assert.Contains(error,r.Errors);Assert.Null(r.Data);Assert.False(r.Success);
    }
    [UnitTheory][InlineData(false)][InlineData(true)]
    public async Task GivenValidTypedSets_WhenHandling_ThenBindTrustedContextAndReturnOnlySixOrderedFields(bool empty)
    {
        var c=Command();if(empty){c.RecordIds=[];c.FolderIds=[];c.CollectionIds=[];}else c.RecordIds=[Guid.NewGuid(),Guid.NewGuid()];
        ProfileAssociationRequest? captured=null;CancellationToken capturedToken=default;using var source=new CancellationTokenSource();var store=new Mock<IProfileAssociationStore>(MockBehavior.Strict);
        store.Setup(x=>x.SetAsync(It.IsAny<ProfileAssociationRequest>(),It.IsAny<CancellationToken>())).Returns<ProfileAssociationRequest,CancellationToken>((request,token)=>{captured=request;capturedToken=token;return Task.FromResult(new VaultResult<ProfileAssociationDetails>(new(id,2,9,c.RecordIds.Reverse().ToArray(),c.FolderIds,c.CollectionIds)));});
        var r=await Handler(store.Object).HandleAsync(c,source.Token);Assert.True(r.Success);Assert.Contains("profile_associations_set",r.Messages);Assert.Equal(id,r.Data!.ProfileId);Assert.Equal(2,r.Data.Revision);Assert.Equal(9,r.Data.ServerSequence);Assert.Equal(c.RecordIds.Order(),r.Data.RecordIds);Assert.Equal(c.FolderIds.Order(),r.Data.FolderIds);Assert.Equal(c.CollectionIds.Order(),r.Data.CollectionIds);Assert.Equal(actor,captured!.Actor);Assert.Equal(id,captured.ProfileId);Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925",captured.AccessVerifier);Assert.Equal(c.ToInput(),captured.Input);Assert.Equal(source.Token,capturedToken);
        using var body=JsonDocument.Parse(JsonSerializer.Serialize(c,ProtectionFixture.Json));Assert.Equal(new[]{"collectionIds","expectedRevision","folderIds","recordIds"},body.RootElement.EnumerateObject().Select(x=>x.Name).Order());
        using var output=JsonDocument.Parse(JsonSerializer.Serialize(r.Data,ProtectionFixture.Json));Assert.Equal(new[]{"collectionIds","folderIds","profileId","recordIds","revision","serverSequence"},output.RootElement.EnumerateObject().Select(x=>x.Name).Order());
    }
    [UnitTheory][InlineData("not_found")][InlineData("vault_access_denied")][InlineData("revision_conflict")][InlineData("validation_failed")][InlineData("persistence_unavailable")]
    public async Task GivenStoreError_WhenHandling_ThenForwardStableErrorWithoutPayload(string error)
    {var store=Store(new(Error:error));var r=await Handler(store.Object).HandleAsync(Command());Assert.Contains(error,r.Errors);Assert.Null(r.Data);}
    [UnitTheory][InlineData("null")][InlineData("id")][InlineData("revision")][InlineData("unsafeRevision")][InlineData("sequence")][InlineData("unsafeSequence")][InlineData("nullRecords")][InlineData("nullFolders")][InlineData("nullCollections")][InlineData("zeroRecord")][InlineData("duplicateFolder")][InlineData("duplicateCollection")][InlineData("extraRecord")][InlineData("missingRecord")][InlineData("wrongFolder")][InlineData("wrongCollection")]
    public async Task GivenCorruptOrMismatchedStoreResult_WhenHandling_ThenFailClosedWithoutPartialData(string kind)
    {
        var c=Command();var d=new ProfileAssociationDetails(id,2,9,c.RecordIds,c.FolderIds,c.CollectionIds);d=kind switch {
            "null"=>null!,"id"=>d with {ProfileId=Guid.NewGuid()},"revision"=>d with {Revision=1},"unsafeRevision"=>d with {Revision=ProtocolBinary.MaxInteger+1},"sequence"=>d with {ServerSequence=0},"unsafeSequence"=>d with {ServerSequence=ProtocolBinary.MaxInteger+1},
            "nullRecords"=>d with {RecordIds=null!},"nullFolders"=>d with {FolderIds=null!},"nullCollections"=>d with {CollectionIds=null!},"zeroRecord"=>d with {RecordIds=[Guid.Empty]},"duplicateFolder"=>d with {FolderIds=[id,id]},"duplicateCollection"=>d with {CollectionIds=[id,id]},"extraRecord"=>d with {RecordIds=[..c.RecordIds,Guid.NewGuid()]},"missingRecord"=>d with {RecordIds=[]},"wrongFolder"=>d with {FolderIds=[Guid.NewGuid()]},_=>d with {CollectionIds=[Guid.NewGuid()]}};
        var r=await Handler(Store(new(d)).Object).HandleAsync(c);Assert.Contains("persistence_unavailable",r.Errors);Assert.Null(r.Data);Assert.False(r.Success);
    }
    [UnitTheory][InlineData("actor")][InlineData("vaultAccess")][InlineData("profileId")][InlineData("envelope")][InlineData("keyWrappers")][InlineData("editedAt")]
    public void GivenBodyAttemptsToForgeContextOrContent_WhenParsing_ThenRejectUnknownFields(string field)
    {var raw=JsonSerializer.Serialize(Command(),ProtectionFixture.Json).Insert(1,"\""+field+"\":null,");Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<SetProfileAssociationsCommand>(raw,ProtectionFixture.Json));}
    [UnitTheory][InlineData("expectedRevision")][InlineData("recordIds")][InlineData("folderIds")][InlineData("collectionIds")]
    public void GivenMissingRequiredField_WhenParsing_ThenReject(string field)
    {var raw=JsonSerializer.Serialize(Command(),ProtectionFixture.Json);var data=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(raw)!;data.Remove(field);Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<SetProfileAssociationsCommand>(JsonSerializer.Serialize(data),ProtectionFixture.Json));}
    [UnitTheory][InlineData("profile_associations_set",200)][InlineData("authentication_required",401)][InlineData("vault_access_required",401)][InlineData("vault_access_denied",403)][InlineData("validation_failed",400)][InlineData("not_found",404)][InlineData("revision_conflict",409)][InlineData("persistence_unavailable",503)][InlineData("identity_unavailable",503)]
    public void GivenOutcome_WhenMapping_ThenUseStableStatus(string outcome,int status)=>Assert.Equal(status,SetProfileAssociationsMessages.StatusCodes[outcome]);
    [UnitFact]
    public async Task GivenCancelledCaller_WhenHandling_ThenPropagateWithoutStore()
    {using var source=new CancellationTokenSource();source.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(new Mock<IProfileAssociationStore>(MockBehavior.Strict).Object).HandleAsync(Command(),source.Token));}
    private SetProfileAssociationsCommand Command(){var c=new SetProfileAssociationsCommand{ExpectedRevision=1,RecordIds=[id],FolderIds=[id],CollectionIds=[id]};c.SetContext(actor,Access,id);return c;}
    private static SetProfileAssociationsHandler Handler(IProfileAssociationStore store)=>new(new SetProfileAssociationsValidator(),store);
    private static Mock<IProfileAssociationStore> Store(VaultResult<ProfileAssociationDetails> result){var store=new Mock<IProfileAssociationStore>();store.Setup(x=>x.SetAsync(It.IsAny<ProfileAssociationRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(result);return store;}
}
