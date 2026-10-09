using System.Text.Json;
using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class ProfileAccessHandlerTests
{
    private const string Access="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly string Proof=ProtocolBinary.Encode(new byte[64]);
    private readonly Guid actor=Guid.NewGuid(), account=Guid.NewGuid(), id=Guid.NewGuid(), challengeId=Guid.NewGuid();
    private static readonly DateTimeOffset Issued=new(2026,10,8,12,0,0,TimeSpan.Zero);

    [UnitTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenNativeProfile_WhenChallengingAndOpening_ThenBindTrustedContextAndReturnOnlySelectedMaterial(string mode)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var material=Material(owner,scoped,mode);var command=ChallengeCommand();var open=OpenCommand();var details=new ProfileChallengeDetails(Challenge(),material);var access=Details(material);
        ProfileChallengeRequest? capturedChallenge=null;ProfileAccessRequest? capturedOpen=null;CancellationToken challengeToken=default,openToken=default;using var source=new CancellationTokenSource();
        var store=new Mock<IProfileAccessStore>(MockBehavior.Strict);
        store.Setup(x=>x.ChallengeAsync(It.IsAny<ProfileChallengeRequest>(),It.IsAny<CancellationToken>())).Returns<ProfileChallengeRequest,CancellationToken>((r,t)=>{capturedChallenge=r;challengeToken=t;return Task.FromResult(new VaultResult<ProfileChallengeDetails>(details));});
        store.Setup(x=>x.OpenAsync(It.IsAny<ProfileAccessRequest>(),It.IsAny<CancellationToken>())).Returns<ProfileAccessRequest,CancellationToken>((r,t)=>{capturedOpen=r;openToken=t;return Task.FromResult(new VaultResult<ProfileAccessDetails>(access));});
        var challenge=await ChallengeHandler(store.Object).HandleAsync(command,source.Token);var result=await OpenHandler(store.Object).HandleAsync(open,source.Token);
        Assert.True(challenge.Success);Assert.Contains("profile_access_challenge_issued",challenge.Messages);Assert.Equal(details.Challenge,challenge.Data!.Challenge);Assert.Equal(material,challenge.Data.Profile);
        Assert.Equal(new ProfileChallengeRequest(actor,id,1,Access),capturedChallenge);Assert.Equal(source.Token,challengeToken);
        Assert.True(result.Success);Assert.Contains("profile_access_opened",result.Messages);Assert.Equal(account,result.Data!.AccountId);Assert.Equal(Access,result.Data.VaultAccess);Assert.Equal(access.Profile,result.Data.Profile);Assert.Equal(Issued,result.Data.IssuedAt);Assert.Equal(access.ExpiresAt,result.Data.ExpiresAt);
        Assert.Equal(actor,capturedOpen!.Actor);Assert.Equal(id,capturedOpen.ProfileId);Assert.Equal(1,capturedOpen.ExpectedRevision);Assert.Equal(challengeId,capturedOpen.ChallengeId);Assert.Equal(Proof,capturedOpen.Proof);Assert.Same(RawBody,capturedOpen.RawBody);Assert.Equal(source.Token,openToken);
        AssertFields(command,"expectedRevision","requestHash");AssertFields(open,"expectedRevision");AssertFields(challenge.Data,"challenge","profile");AssertFields(challenge.Data.Profile,"accountId","envelope","keyWrappers","profileId","revision");AssertFields(result.Data,"accountId","expiresAt","issuedAt","profile","vaultAccess");AssertFields(result.Data.Profile,"collectionIds","editedAt","envelope","folderIds","keyWrappers","profileId","recordIds","revision","serverSequence");
        var serialized=JsonSerializer.Serialize(result.Data,ProtectionFixture.Json);Assert.DoesNotContain("accessVerifier",serialized);Assert.DoesNotContain("internalId",serialized);
    }
    [UnitTheory][InlineData(false)][InlineData(true)]
    public async Task GivenDisabledRenewalOrEmptyTypedSets_WhenOpening_ThenAcceptValidSelectedContext(bool empty)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var d=Details(Material(owner,scoped,"Master")) with {ExpiresAt=null};if(empty)d=d with {Profile=d.Profile with {RecordIds=[],FolderIds=[],CollectionIds=[]}};
        var r=await OpenHandler(Store(open:new(d))).HandleAsync(OpenCommand());Assert.True(r.Success);Assert.Null(r.Data!.ExpiresAt);Assert.Equal(d.Profile,r.Data.Profile);
    }
    [UnitTheory][InlineData("actor","authentication_required")][InlineData("id","validation_failed")][InlineData("revision","validation_failed")][InlineData("unsafe","validation_failed")][InlineData("hash","validation_failed")][InlineData("nullHash","validation_failed")]
    public async Task GivenInvalidChallengeInput_WhenHandling_ThenRejectBeforeStore(string invalid,string error)
    {
        var c=ChallengeCommand();c.SetContext(invalid=="actor"?Guid.Empty:actor,invalid=="id"?Guid.Empty:id);if(invalid=="revision")c.ExpectedRevision=0;if(invalid=="unsafe")c.ExpectedRevision=ProtocolBinary.MaxInteger+1;if(invalid=="hash")c.RequestHash=Access+"=";if(invalid=="nullHash")c.RequestHash=null!;
        var r=await ChallengeHandler(new Mock<IProfileAccessStore>(MockBehavior.Strict).Object).HandleAsync(c);Assert.Contains(error,r.Errors);Assert.Null(r.Data);Assert.False(r.Success);
    }
    [UnitTheory][InlineData("actor","authentication_required")][InlineData("id","validation_failed")][InlineData("revision","validation_failed")][InlineData("unsafe","validation_failed")][InlineData("challenge","validation_failed")][InlineData("proof","validation_failed")][InlineData("nullProof","validation_failed")][InlineData("body","validation_failed")][InlineData("nullBody","validation_failed")][InlineData("oversized","validation_failed")]
    public async Task GivenInvalidOpenInput_WhenHandling_ThenRejectBeforeStore(string invalid,string error)
    {
        var c=OpenCommand();if(invalid=="revision")c.ExpectedRevision=0;if(invalid=="unsafe")c.ExpectedRevision=ProtocolBinary.MaxInteger+1;
        c.SetContext(invalid=="actor"?Guid.Empty:actor,invalid=="id"?Guid.Empty:id,invalid=="challenge"?Guid.Empty:challengeId,invalid=="proof"?"bad":invalid=="nullProof"?null!:Proof,invalid=="body"?[]:invalid=="nullBody"?null!:invalid=="oversized"?new byte[1048577]:RawBody);
        var r=await OpenHandler(new Mock<IProfileAccessStore>(MockBehavior.Strict).Object).HandleAsync(c);Assert.Contains(error,r.Errors);Assert.Null(r.Data);Assert.False(r.Success);
    }
    [UnitTheory][InlineData("authentication_required")][InlineData("vault_proof_rejected")][InlineData("validation_failed")][InlineData("not_found")][InlineData("revision_conflict")][InlineData("persistence_unavailable")][InlineData("identity_unavailable")][InlineData("untrusted_database_text")][InlineData("profile_access_opened")]
    public async Task GivenStoreErrorEvenAlongsideData_WhenHandling_ThenReturnOnlyAllowlistedFailure(string error)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var m=Material(owner,scoped,"Master");var store=Store(new(new(Challenge(),m),error),new(Details(m),error));var expected=error is "untrusted_database_text" or "profile_access_opened"?"persistence_unavailable":error;
        var c=await ChallengeHandler(store).HandleAsync(ChallengeCommand());var o=await OpenHandler(store).HandleAsync(OpenCommand());Assert.Contains(expected,c.Errors);Assert.Contains(expected,o.Errors);Assert.Null(c.Data);Assert.Null(o.Data);Assert.False(c.Success);Assert.False(o.Success);
    }
    [UnitTheory][InlineData("result")][InlineData("data")][InlineData("challenge")][InlineData("profile")][InlineData("format")][InlineData("challengeId")][InlineData("nonce")][InlineData("operation")][InlineData("identity")][InlineData("account")][InlineData("scopeKind")][InlineData("scopeId")][InlineData("epoch")][InlineData("revision")][InlineData("generation")][InlineData("hash")][InlineData("issued")][InlineData("unsafeIssued")][InlineData("expires")][InlineData("profileId")][InlineData("profileAccount")][InlineData("profileRevision")][InlineData("envelope")][InlineData("wrappers")][InlineData("recipient")][InlineData("wrapperId")]
    public async Task GivenCorruptChallengeResult_WhenHandling_ThenFailClosed(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var m=Material(owner,scoped,"Master");var c=Challenge();
        c=kind switch {"format"=>c with {Format="invalid"},"challengeId"=>c with {ChallengeId=Guid.Empty},"nonce"=>c with {Nonce="bad"},"operation"=>c with {Operation="unlock-account"},"identity"=>c with {IdentityId=Guid.NewGuid()},"account"=>c with {AccountId=Guid.NewGuid()},"scopeKind"=>c with {ScopeKind="account"},"scopeId"=>c with {ScopeId=Guid.NewGuid()},"epoch"=>c with {KeyEpoch=2},"revision"=>c with {ProtectionRevision=2},"generation"=>c with {Generation=1},"hash"=>c with {RequestHash=ProtocolBinary.Encode(Enumerable.Repeat((byte)1,32).ToArray())},"issued"=>c with {IssuedAt=-1,ExpiresAt=59},"unsafeIssued"=>c with {IssuedAt=ProtocolBinary.MaxInteger,ExpiresAt=ProtocolBinary.MaxInteger+60},"expires"=>c with {ExpiresAt=c.ExpiresAt+1},_=>c};
        m=kind switch {"profileId"=>m with {ProfileId=Guid.NewGuid()},"profileAccount"=>m with {AccountId=Guid.Empty},"profileRevision"=>m with {Revision=2},"envelope"=>m with {Envelope=null!},"wrappers"=>m with {KeyWrappers=null!},"recipient"=>m with {KeyWrappers=m.KeyWrappers with {MasterKeyWrapper=m.KeyWrappers.MasterKeyWrapper with {RecipientIdentityId=Guid.NewGuid()}}},"wrapperId"=>m with {KeyWrappers=m.KeyWrappers with {MasterKeyWrapper=m.KeyWrappers.MasterKeyWrapper with {GrantId=Guid.NewGuid()}}},_=>m};
        var d=kind=="data"?null:new ProfileChallengeDetails(kind=="challenge"?null!:c,kind=="profile"?null!:m);var r=await ChallengeHandler(Store(challenge:kind=="result"?null!:new(d))).HandleAsync(ChallengeCommand());Assert.Contains("persistence_unavailable",r.Errors);Assert.Null(r.Data);Assert.False(r.Success);
    }
    [UnitTheory][InlineData("result")][InlineData("data")][InlineData("profile")][InlineData("account")][InlineData("handle")][InlineData("nullHandle")][InlineData("issuedDefault")][InlineData("issuedBeforeEpoch")][InlineData("issuedOffset")][InlineData("issuedPrecision")][InlineData("expiresBefore")][InlineData("expiresEqual")][InlineData("expiresOffset")][InlineData("expiresPrecision")][InlineData("profileId")][InlineData("revision")][InlineData("sequence")][InlineData("unsafeSequence")][InlineData("editedDefault")][InlineData("editedOffset")][InlineData("editedPrecision")][InlineData("envelope")][InlineData("wrappers")][InlineData("recipient")][InlineData("wrapperId")][InlineData("nullRecords")][InlineData("nullFolders")][InlineData("nullCollections")][InlineData("zeroRecord")][InlineData("duplicateFolder")][InlineData("duplicateCollection")]
    public async Task GivenCorruptOpenResult_WhenHandling_ThenFailClosedWithoutHandle(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var d=Details(Material(owner,scoped,"Master"));var p=d.Profile;
        p=kind switch {"profileId"=>p with {ProfileId=Guid.NewGuid()},"revision"=>p with {Revision=2},"sequence"=>p with {ServerSequence=0},"unsafeSequence"=>p with {ServerSequence=ProtocolBinary.MaxInteger+1},"editedDefault"=>p with {EditedAt=default},"editedOffset"=>p with {EditedAt=Issued.ToOffset(TimeSpan.FromHours(1))},"editedPrecision"=>p with {EditedAt=Issued.AddTicks(1)},"envelope"=>p with {Envelope=null!},"wrappers"=>p with {KeyWrappers=null!},"recipient"=>p with {KeyWrappers=p.KeyWrappers with {MasterKeyWrapper=p.KeyWrappers.MasterKeyWrapper with {RecipientIdentityId=Guid.NewGuid()}}},"wrapperId"=>p with {KeyWrappers=p.KeyWrappers with {MasterKeyWrapper=p.KeyWrappers.MasterKeyWrapper with {GrantId=Guid.NewGuid()}}},"nullRecords"=>p with {RecordIds=null!},"nullFolders"=>p with {FolderIds=null!},"nullCollections"=>p with {CollectionIds=null!},"zeroRecord"=>p with {RecordIds=[Guid.Empty]},"duplicateFolder"=>p with {FolderIds=[id,id]},"duplicateCollection"=>p with {CollectionIds=[id,id]},_=>p};
        d=d with {Profile=kind=="profile"?null!:p};d=kind switch {"account"=>d with {AccountId=Guid.Empty},"handle"=>d with {Access=Access+"="},"nullHandle"=>d with {Access=null!},"issuedDefault"=>d with {IssuedAt=default},"issuedBeforeEpoch"=>d with {IssuedAt=DateTimeOffset.UnixEpoch.AddSeconds(-1)},"issuedOffset"=>d with {IssuedAt=Issued.ToOffset(TimeSpan.FromHours(1))},"issuedPrecision"=>d with {IssuedAt=Issued.AddTicks(1)},"expiresBefore"=>d with {ExpiresAt=Issued.AddSeconds(-1)},"expiresEqual"=>d with {ExpiresAt=Issued},"expiresOffset"=>d with {ExpiresAt=Issued.AddMinutes(1).ToOffset(TimeSpan.FromHours(1))},"expiresPrecision"=>d with {ExpiresAt=Issued.AddMinutes(1).AddTicks(1)},_=>d};
        var r=await OpenHandler(Store(open:kind=="result"?null!:new(kind=="data"?null:d))).HandleAsync(OpenCommand());Assert.Contains("persistence_unavailable",r.Errors);Assert.Null(r.Data);Assert.False(r.Success);
    }
    [UnitTheory][InlineData("actor")][InlineData("profileId")][InlineData("challengeId")][InlineData("proof")][InlineData("rawBody")][InlineData("vaultAccess")][InlineData("password")][InlineData("accountId")]
    public void GivenForgedBodyContext_WhenParsing_ThenRejectUnknownFields(string field)
    {
        var extra="\""+field+"\":null,";Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<IssueProfileAccessChallengeCommand>("{"+extra+"\"expectedRevision\":1,\"requestHash\":\""+Access+"\"}",ProtectionFixture.Json));Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<OpenProfileAccessCommand>("{"+extra+"\"expectedRevision\":1}",ProtectionFixture.Json));
    }
    [UnitTheory][InlineData("{}")][InlineData("{\"requestHash\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}")][InlineData("{\"expectedRevision\":1}")][InlineData("{\"expectedRevision\":\"1\",\"requestHash\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}")][InlineData("{\"ExpectedRevision\":1,\"requestHash\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}")][InlineData("{\"expectedRevision\":1,\"expectedRevision\":2,\"requestHash\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}")]
    public void GivenMalformedChallengeWireBody_WhenParsing_ThenReject(string raw)=>Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<IssueProfileAccessChallengeCommand>(raw,ProtectionFixture.Json));
    [UnitTheory][InlineData("{}")][InlineData("{\"expectedRevision\":\"1\"}")][InlineData("{\"ExpectedRevision\":1}")][InlineData("{\"expectedRevision\":1,\"expectedRevision\":2}")]
    public void GivenMalformedOpenWireBody_WhenParsing_ThenReject(string raw)=>Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<OpenProfileAccessCommand>(raw,ProtectionFixture.Json));
    [UnitTheory][InlineData("profile_access_opened",200)][InlineData("profile_access_challenge_issued",201)][InlineData("authentication_required",401)][InlineData("vault_proof_rejected",401)][InlineData("validation_failed",400)][InlineData("not_found",404)][InlineData("revision_conflict",409)][InlineData("persistence_unavailable",503)][InlineData("identity_unavailable",503)]
    public void GivenOutcome_WhenMapping_ThenUseStableStatus(string outcome,int status)=>Assert.Equal(status,ProfileAccessMessages.StatusCodes[outcome]);
    [UnitFact]
    public async Task GivenCancelledCaller_WhenHandling_ThenPropagateWithoutStore()
    {
        using var source=new CancellationTokenSource();source.Cancel();var store=new Mock<IProfileAccessStore>(MockBehavior.Strict).Object;await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ChallengeHandler(store).HandleAsync(ChallengeCommand(),source.Token));await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>OpenHandler(store).HandleAsync(OpenCommand(),source.Token));
    }
    [UnitFact]
    public async Task GivenDependencyCancellation_WhenHandling_ThenPropagateWithoutPayload()
    {
        var store=new Mock<IProfileAccessStore>(MockBehavior.Strict);store.Setup(x=>x.ChallengeAsync(It.IsAny<ProfileChallengeRequest>(),It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());store.Setup(x=>x.OpenAsync(It.IsAny<ProfileAccessRequest>(),It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ChallengeHandler(store.Object).HandleAsync(ChallengeCommand()));await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>OpenHandler(store.Object).HandleAsync(OpenCommand()));
    }
    private static readonly byte[] RawBody="{\"expectedRevision\":1}"u8.ToArray();
    private IssueProfileAccessChallengeCommand ChallengeCommand(){var c=new IssueProfileAccessChallengeCommand {ExpectedRevision=1,RequestHash=Access};c.SetContext(actor,id);return c;}
    private OpenProfileAccessCommand OpenCommand(){var c=new OpenProfileAccessCommand {ExpectedRevision=1};c.SetContext(actor,id,challengeId,Proof,RawBody);return c;}
    private ProfileAccessMaterial Material(ProtectionFixture owner,ProtectionFixture scoped,string mode)=>new(account,id,1,new("cerberus-content-v1",1,Access,"AAAAAAAAAAAAAAAA","AQID","AAAAAAAAAAAAAAAAAAAAAA"),new(mode,scoped.Material.UnlockVerifier,owner.Wrap(account,"profile",id,actor),mode=="PerProfile"?scoped.Material.PasswordWrapper:null));
    private VaultProofChallenge Challenge()=>new("cerberus-challenge-v1",challengeId,Access,"unlock-profile",actor,account,"profile",id,1,1,null,Access,Issued.ToUnixTimeSeconds(),Issued.ToUnixTimeSeconds()+60);
    private static ProfileAccessDetails Details(ProfileAccessMaterial m)=>new(m.AccountId,Access,Issued,Issued.AddMinutes(1),new(m.ProfileId,m.Revision,9,Issued,m.Envelope,m.KeyWrappers,[m.ProfileId],[m.ProfileId],[m.ProfileId]));
    private static IssueProfileAccessChallengeHandler ChallengeHandler(IProfileAccessStore s)=>new(new IssueProfileAccessChallengeValidator(),s);
    private static OpenProfileAccessHandler OpenHandler(IProfileAccessStore s)=>new(new OpenProfileAccessValidator(),s);
    private static IProfileAccessStore Store(VaultResult<ProfileChallengeDetails>? challenge=null,VaultResult<ProfileAccessDetails>? open=null)
    {var s=new Mock<IProfileAccessStore>(MockBehavior.Strict);s.Setup(x=>x.ChallengeAsync(It.IsAny<ProfileChallengeRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(challenge!);s.Setup(x=>x.OpenAsync(It.IsAny<ProfileAccessRequest>(),It.IsAny<CancellationToken>())).ReturnsAsync(open!);return s.Object;}
    private static void AssertFields(object value,params string[] names){using var d=JsonDocument.Parse(JsonSerializer.Serialize(value,ProtectionFixture.Json));Assert.Equal(names.Order(),d.RootElement.EnumerateObject().Select(x=>x.Name).Order());}
}
