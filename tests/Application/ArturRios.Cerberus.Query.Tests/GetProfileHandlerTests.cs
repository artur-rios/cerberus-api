using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Query.Profiles;
using ArturRios.Cerberus.TestSupport;
using Moq;
namespace ArturRios.Cerberus.Query.Tests;

public class GetProfileHandlerTests
{
    private const string Access="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Verifier="66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925";
    private static readonly Guid Actor=Guid.NewGuid(), Target=Guid.NewGuid();
    [UnitTheory][InlineData("actor","authentication_required")][InlineData("missing","vault_access_required")]
    [InlineData("access","validation_failed")][InlineData("target","validation_failed")]
    public async Task GivenInvalidTrustedContext_WhenGettingProfile_ThenRejectBeforeStore(string kind,string error)
    {var store=new Mock<IProfileReadStore>(MockBehavior.Strict);var r=await new GetProfileHandler(store.Object).HandleAsync(new(kind=="actor"?Guid.Empty:Actor,kind=="missing"?null:kind=="access"?"bad":Access,kind=="target"?Guid.Empty:Target));Assert.Contains(error,r.Errors);Assert.Null(r.Data);store.VerifyNoOtherCalls();}
    [UnitFact]
    public async Task GivenAuthorizedOpaqueProfileAndVisibleLinks_WhenGetting_ThenReturnExactPublicData()
    {var data=Details();var r=await Handler(new(data)).HandleAsync(new(Actor,Access,Target));Assert.True(r.Success);Assert.Equal(Target,r.Data!.ProfileId);Assert.Equal(data.Profile.Revision,r.Data.Revision);Assert.Equal(data.Profile.ServerSequence,r.Data.ServerSequence);Assert.Equal(data.Profile.EditedAt,r.Data.EditedAt);Assert.Equal(JsonSerializer.Deserialize<EncryptedEnvelope>(data.Profile.Envelope,ProtectionFixture.Json),r.Data.Envelope);Assert.Equal(JsonSerializer.Deserialize<ProfileKeyWrappers>(data.Profile.KeyWrappers,ProtectionFixture.Json),r.Data.KeyWrappers);Assert.Equal(data.RecordIds,r.Data.RecordIds);Assert.Equal(data.FolderIds,r.Data.FolderIds);Assert.Equal(data.CollectionIds,r.Data.CollectionIds);}
    [UnitTheory][InlineData("not_found")][InlineData("vault_access_denied")][InlineData("persistence_unavailable")]
    public async Task GivenStoreFailure_WhenGetting_ThenNoCiphertextOrLinks(string error)
    {var r=await Handler(new(Error:error)).HandleAsync(new(Actor,Access,Target));Assert.Contains(error,r.Errors);Assert.Null(r.Data);}
    [UnitTheory][InlineData("missing")][InlineData("nullProfile")][InlineData("target")][InlineData("revision")][InlineData("largeRevision")]
    [InlineData("sequence")][InlineData("largeSequence")][InlineData("time")][InlineData("offset")][InlineData("precision")]
    [InlineData("envelope")][InlineData("nullEnvelopeBytes")][InlineData("nullEnvelope")][InlineData("invalidEnvelope")][InlineData("duplicateJson")]
    [InlineData("wrappers")][InlineData("nullWrapperBytes")][InlineData("nullWrapper")][InlineData("invalidWrapper")]
    [InlineData("recipient")][InlineData("grant")][InlineData("epoch")][InlineData("grantRevision")]
    [InlineData("nullRecords")][InlineData("nullFolders")][InlineData("nullCollections")][InlineData("emptyId")][InlineData("duplicateId")]
    public async Task GivenCorruptStoredContract_WhenGetting_ThenUnavailableWithoutPartialData(string kind)
    {
        var d=Details();var p=d.Profile;var w=JsonSerializer.Deserialize<ProfileKeyWrappers>(p.KeyWrappers,ProtectionFixture.Json)!;var env=JsonSerializer.Deserialize<EncryptedEnvelope>(p.Envelope,ProtectionFixture.Json)!;
        p=kind switch {
            "nullProfile"=>null!,"target"=>p with {ProfileId=Guid.NewGuid()},"revision"=>p with {Revision=0},"largeRevision"=>p with {Revision=long.MaxValue},"sequence"=>p with {ServerSequence=0},"largeSequence"=>p with {ServerSequence=long.MaxValue},
            "time"=>p with {EditedAt=default},"offset"=>p with {EditedAt=p.EditedAt.ToOffset(TimeSpan.FromHours(1))},"precision"=>p with {EditedAt=p.EditedAt.AddTicks(1)},
            "envelope"=>p with {Envelope="bad"u8.ToArray()},"nullEnvelopeBytes"=>p with {Envelope=null!},"nullEnvelope"=>p with {Envelope="null"u8.ToArray()},"invalidEnvelope"=>p with {Envelope=Bytes(env with {Format="unknown"})},"duplicateJson"=>p with {Envelope=System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(p.Envelope).Replace("{","{\"format\":\"bad\","))},
            "wrappers"=>p with {KeyWrappers="bad"u8.ToArray()},"nullWrapperBytes"=>p with {KeyWrappers=null!},"nullWrapper"=>p with {KeyWrappers="null"u8.ToArray()},"invalidWrapper"=>p with {KeyWrappers=Bytes(w with {UnlockMode="unknown"})},
            "recipient"=>p with {KeyWrappers=Bytes(w with {MasterKeyWrapper=w.MasterKeyWrapper with {RecipientIdentityId=Guid.NewGuid()}})},"grant"=>p with {KeyWrappers=Bytes(w with {MasterKeyWrapper=w.MasterKeyWrapper with {GrantId=Guid.NewGuid()}})},"epoch"=>p with {Envelope=Bytes(env with {KeyEpoch=2})},"grantRevision"=>p with {KeyWrappers=Bytes(w with {MasterKeyWrapper=w.MasterKeyWrapper with {GrantRevision=2}})},_=>p};
        d=d with {Profile=p};d=kind switch {"nullRecords"=>d with {RecordIds=null!},"nullFolders"=>d with {FolderIds=null!},"nullCollections"=>d with {CollectionIds=null!},"emptyId"=>d with {RecordIds=[Guid.Empty]},"duplicateId"=>d with {CollectionIds=[Target,Target]},_=>d};
        var r=await Handler(kind=="missing"?new():new(d)).HandleAsync(new(Actor,Access,Target));Assert.Contains("persistence_unavailable",r.Errors);Assert.Null(r.Data);
    }
    [UnitFact]
    public async Task GivenCancellation_WhenGetting_ThenPropagate()
    {using var c=new CancellationTokenSource();c.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new GetProfileHandler(new Mock<IProfileReadStore>(MockBehavior.Strict).Object).HandleAsync(new(Actor,Access,Target),c.Token));}
    private static GetProfileHandler Handler(VaultResult<ProfileReadDetails> result)
    {var s=new Mock<IProfileReadStore>(MockBehavior.Strict);s.Setup(x=>x.ReadAsync(new(Actor,Verifier,Target),It.IsAny<CancellationToken>())).ReturnsAsync(result);return new(s.Object);}
    private static ProfileReadDetails Details()
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var envelope=new EncryptedEnvelope("cerberus-content-v1",1,Access,"AAAAAAAAAAAAAAAA","AQID","AAAAAAAAAAAAAAAAAAAAAA");return new(new(Target,1,7,DateTimeOffset.FromUnixTimeSeconds(1780000000),Bytes(envelope),Bytes(new ProfileKeyWrappers("Master",scoped.Material.UnlockVerifier,f.Wrap(Guid.NewGuid(),"profile",Target,Actor),null))),[Guid.NewGuid()],[Guid.NewGuid()],[Guid.NewGuid()]);}
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
}
