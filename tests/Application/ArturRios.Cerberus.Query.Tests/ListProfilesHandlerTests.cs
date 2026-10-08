using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Query.Profiles;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.TestSupport;
using Moq;
namespace ArturRios.Cerberus.Query.Tests;

public class ListProfilesHandlerTests
{
    private const string Access="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Verifier="66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925";
    private static readonly Guid Actor=Guid.NewGuid();
    private static CerberusOptions Options()=>new(){MaxPageSize=100,RegistrationFingerprintKey="fixture-dedicated-key-at-least-32-chars"};
    [UnitTheory][InlineData("actor","authentication_required")][InlineData("missing","vault_access_required")][InlineData("access","validation_failed")]
    [InlineData("zero","validation_failed")][InlineData("negative","validation_failed")][InlineData("large","validation_failed")]
    [InlineData("empty","validation_failed")][InlineData("cursor","validation_failed")][InlineData("huge","validation_failed")]
    public async Task GivenInvalidContext_WhenListing_ThenRejectBeforeStore(string kind,string error)
    {
        var store=new Mock<IProfileListStore>(MockBehavior.Strict);var q=new ListProfilesQuery(kind=="actor"?Guid.Empty:Actor,kind=="missing"?null:kind=="access"?"bad":Access,
            kind switch {"zero"=>0,"negative"=>-1,"large"=>101,_=>null},kind switch {"empty"=>"","cursor"=>"invalid","huge"=>new string('A',2049),_=>null});
        var r=await Handler(store).HandleAsync(q);Assert.Contains(error,r.Errors);Assert.Null(r.Data);store.VerifyNoOtherCalls();
    }
    [UnitFact]
    public async Task GivenPermittedPage_WhenContinuing_ThenReturnExactOpaqueContractsAndBoundCursor()
    {
        var row=Row(1);var store=new Mock<IProfileListStore>(MockBehavior.Strict);
        store.Setup(x=>x.ListAsync(new(Actor,Verifier,1,0,null),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<ProfileListPage>(new([row],3,true)));
        store.Setup(x=>x.ListAsync(new(Actor,Verifier,1,1,3),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<ProfileListPage>(new([Row(3)],3,false)));
        var handler=Handler(store);var r=await handler.HandleAsync(new(Actor,Access,1,null));Assert.True(r.Success);var item=Assert.Single(r.Data!.Items);Assert.Equal(row.ProfileId,item.ProfileId);
        Assert.Equal(JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope,ProtectionFixture.Json),item.Envelope);Assert.Equal(JsonSerializer.Deserialize<ProfileKeyWrappers>(row.KeyWrappers,ProtectionFixture.Json),item.KeyWrappers);
        Assert.NotNull(r.Data.NextCursor);Assert.DoesNotContain(Actor.ToString(),r.Data.NextCursor);var next=await handler.HandleAsync(new(Actor,Access,1,r.Data.NextCursor));Assert.True(next.Success);Assert.Null(next.Data!.NextCursor);Assert.Equal(3,Assert.Single(next.Data.Items).ServerSequence);
    }
    [UnitTheory][InlineData("actor")][InlineData("access")][InlineData("size")][InlineData("tamper")][InlineData("key")]
    public async Task GivenCursorSubstitution_WhenContinuing_ThenRejectBeforePersistence(string kind)
    {
        var codec=new ProfileListCursor(Options());var cursor=codec.Encode(new(Actor,Verifier,1,1,3));
        if(kind=="tamper"){var b=Convert.FromBase64String(cursor.Replace('-','+').Replace('_','/')+new string('=',(4-cursor.Length%4)%4));b[^1]^=1;cursor=ProtocolBinary.Encode(b);}
        var options=Options();if(kind=="key")options.RegistrationFingerprintKey=new string('z',40);
        var store=new Mock<IProfileListStore>(MockBehavior.Strict);var r=await new ListProfilesHandler(store.Object,options,new(options)).HandleAsync(new(kind=="actor"?Guid.NewGuid():Actor,kind=="access"?ProtocolBinary.Encode(new byte[32].Select(_=>(byte)1).ToArray()):Access,kind=="size"?2:1,cursor));
        Assert.Contains("validation_failed",r.Errors);Assert.Null(r.Data);store.VerifyNoOtherCalls();
    }
    [UnitTheory][InlineData("after")][InlineData("boundary")][InlineData("size")][InlineData("actor")][InlineData("verifier")]
    public void GivenAuthenticatedButInvalidCursorTuple_WhenDecoding_ThenReject(string kind)
    {var codec=new ProfileListCursor(Options());var c=new ProfileListContinuation(kind=="actor"?Guid.Empty:Actor,kind=="verifier"?"bad":Verifier,kind=="size"?0:1,kind=="after"?0:1,kind=="boundary"?ProtocolBinary.MaxInteger+1:3);Assert.False(codec.TryDecode(codec.Encode(c),out _));}
    [UnitTheory][InlineData("not_found")][InlineData("vault_access_denied")][InlineData("persistence_unavailable")]
    public async Task GivenStoreFailure_WhenListing_ThenNoPayload(string error)
    {var store=MockStore(new(Error:error));var r=await Handler(store).HandleAsync(new(Actor,Access,null,null));Assert.Contains(error,r.Errors);Assert.Null(r.Data);}
    [UnitTheory][InlineData("missing")][InlineData("nullItems")][InlineData("envelope")][InlineData("wrappers")][InlineData("nullEnvelope")][InlineData("nullWrapper")]
    [InlineData("invalidEnvelope")][InlineData("invalidWrapper")][InlineData("extraJson")][InlineData("id")][InlineData("revision")][InlineData("sequence")]
    [InlineData("time")][InlineData("offset")][InlineData("precision")][InlineData("order")][InlineData("duplicate")][InlineData("count")][InlineData("boundary")][InlineData("emptyMore")][InlineData("grant")][InlineData("epoch")]
    public async Task GivenCorruptStoredPage_WhenListing_ThenFailWholePageClosed(string kind)
    {
        var row=Row(1);var wrapper=JsonSerializer.Deserialize<ProfileKeyWrappers>(row.KeyWrappers,ProtectionFixture.Json)!;
        row=kind switch {
            "envelope"=>row with {Envelope=Encoding.UTF8.GetBytes("bad")},"wrappers"=>row with {KeyWrappers=Encoding.UTF8.GetBytes("bad")},
            "nullEnvelope"=>row with {Envelope="null"u8.ToArray()},"nullWrapper"=>row with {KeyWrappers="null"u8.ToArray()},
            "invalidEnvelope"=>row with {Envelope=JsonSerializer.SerializeToUtf8Bytes(new EncryptedEnvelope("bad",1,"a","b","c","d"),ProtectionFixture.Json)},
            "invalidWrapper"=>row with {KeyWrappers=JsonSerializer.SerializeToUtf8Bytes(wrapper with {UnlockMode="bad"},ProtectionFixture.Json)},
            "extraJson"=>row with {Envelope=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(row.Envelope).Replace("{","{\"extra\":1,"))},
            "id"=>row with {ProfileId=Guid.Empty},"revision"=>row with {Revision=0},"sequence"=>row with {ServerSequence=0},
            "time"=>row with {EditedAt=default},"offset"=>row with {EditedAt=row.EditedAt.ToOffset(TimeSpan.FromHours(1))},"precision"=>row with {EditedAt=row.EditedAt.AddTicks(1)},
            "grant"=>row with {KeyWrappers=JsonSerializer.SerializeToUtf8Bytes(wrapper with {MasterKeyWrapper=wrapper.MasterKeyWrapper with {GrantId=Guid.NewGuid()}},ProtectionFixture.Json)},
            "epoch"=>row with {Envelope=JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope,ProtectionFixture.Json)! with {KeyEpoch=2},ProtectionFixture.Json)},_=>row};
        IReadOnlyList<ProfileListRow> rows=kind switch {"nullItems"=>null!,"order"=>[Row(2),row],"duplicate"=>[row,row with {ServerSequence=2}],"count"=>Enumerable.Range(1,51).Select(x=>Row(x)).ToArray(),"emptyMore"=>[],_=>[row]};
        var store=MockStore(kind=="missing"?new():new(new(rows,kind=="boundary"?-1:100,kind=="emptyMore")));
        var r=await Handler(store).HandleAsync(new(Actor,Access,null,null));Assert.Contains("persistence_unavailable",r.Errors);Assert.Null(r.Data);
    }
    [UnitFact]
    public async Task GivenEmptyPageAndSmallDeploymentBound_WhenListing_ThenUseBoundedDefaultAndEnd()
    {var options=Options();options.MaxPageSize=2;var store=new Mock<IProfileListStore>(MockBehavior.Strict);store.Setup(x=>x.ListAsync(new(Actor,Verifier,2,0,null),It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<ProfileListPage>(new([],0,false)));var r=await new ListProfilesHandler(store.Object,options,new(options)).HandleAsync(new(Actor,Access,null,null));Assert.True(r.Success);Assert.Empty(r.Data!.Items);Assert.Null(r.Data.NextCursor);}
    [UnitFact]
    public async Task GivenCancellation_WhenListing_ThenPropagate()
    {using var c=new CancellationTokenSource();c.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Handler(new Mock<IProfileListStore>(MockBehavior.Strict)).HandleAsync(new(Actor,Access,null,null),c.Token));}
    private static Mock<IProfileListStore> MockStore(VaultResult<ProfileListPage> result)
    {var s=new Mock<IProfileListStore>(MockBehavior.Strict);s.Setup(x=>x.ListAsync(new(Actor,Verifier,50,0,null),It.IsAny<CancellationToken>())).ReturnsAsync(result);return s;}
    private static ListProfilesHandler Handler(Mock<IProfileListStore> store)=>new(store.Object,Options(),new(Options()));
    private static ProfileListRow Row(long seq)
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var id=Guid.NewGuid();var env=new EncryptedEnvelope("cerberus-content-v1",1,Access,"AAAAAAAAAAAAAAAA","AQID","AAAAAAAAAAAAAAAAAAAAAA");return new(id,1,seq,DateTimeOffset.FromUnixTimeSeconds(1780000000),JsonSerializer.SerializeToUtf8Bytes(env,ProtectionFixture.Json),JsonSerializer.SerializeToUtf8Bytes(new ProfileKeyWrappers("Master",scoped.Material.UnlockVerifier,f.Wrap(Guid.NewGuid(),"profile",id,Actor),null),ProtectionFixture.Json));}
}
