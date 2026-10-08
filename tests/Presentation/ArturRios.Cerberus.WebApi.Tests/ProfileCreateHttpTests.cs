using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.Command.Protection;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Configuration.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class ProfileCreateHttpTests(RegistrationApiFixture fixture):WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenNativeInitializedUnlockedOwner_WhenCreatingProfile_ThenReturnOnlyOwnedMetadata(string mode)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(client,false,false);Authorize(RegistrationApiFixture.Token(s.Actor));
        using(var init=await Gateway.Client.PostAsJsonAsync("/api/vault/protection",new{accountId=s.AccountId,expectedAccountRevision=1,material=client.Material}))Assert.Equal(HttpStatusCode.Created,init.StatusCode);
        s=s with {Access=await Unlock(client,1)};var input=Input(s,client,scoped,mode);using var response=await Send(Bytes(input),s.Access);
        Assert.Equal(HttpStatusCode.Created,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);var result=(await response.Content.ReadFromJsonAsync<DataOutput<CreateProfileOutput>>())!;
        Assert.True(result.Success);Assert.Equal(input.ProfileId,result.Data!.ProfileId);Assert.Equal(1,result.Data.Revision);Assert.True(result.Data.ServerSequence>0);
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal(new[]{"editedAt","profileId","revision","serverSequence"},doc.RootElement.GetProperty("data").EnumerateObject().Select(x=>x.Name).Order().ToArray());
        await using var db=fixture.Context();var row=await db.Profiles.SingleAsync(x=>x.PublicId==input.ProfileId);Assert.Equal(s.InternalId,row.AccountId);
        Assert.Equal(input.KeyWrappers,JsonSerializer.Deserialize<ProfileKeyWrappers>(row.KeyWrappers,ProtectionFixture.Json));Assert.Equal(input.Envelope,JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope,ProtectionFixture.Json));
    }
    [FunctionalTheory]
    [InlineData("1999-12-31T23:59:59.9999999Z", "1999-12-31T23:59:59.9999990Z")]
    [InlineData("2000-01-01T00:00:00.0000000Z", "2000-01-01T00:00:00.0000000Z")]
    [InlineData("2000-01-01T00:00:00.0000001Z", "2000-01-01T00:00:00.0000000Z")]
    public async Task GivenSubMicrosecondEditAroundPostgresEpoch_WhenCreating_ThenReturnCommittedNormalizedTimestamp(string timestamp,string normalizedTimestamp)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(client);Authorize(RegistrationApiFixture.Token(s.Actor));
        var time=DateTimeOffset.Parse(timestamp,System.Globalization.CultureInfo.InvariantCulture);
        var input=Input(s,client,scoped) with {EditedAt=time};var expected=DateTimeOffset.Parse(normalizedTimestamp,System.Globalization.CultureInfo.InvariantCulture);
        using var response=await Send(Bytes(input),s.Access);Assert.Equal(HttpStatusCode.Created,response.StatusCode);
        var result=(await response.Content.ReadFromJsonAsync<DataOutput<CreateProfileOutput>>())!.Data!;
        Assert.Equal(expected,result.EditedAt);
        await using var db=fixture.Context();var row=Assert.Single(await db.Profiles.Where(x=>x.AccountId==s.InternalId).ToListAsync());
        Assert.Equal(expected,row.EditedAt);Assert.Equal(input.ProfileId,row.PublicId);
    }

    [FunctionalTheory]
    [InlineData("name")][InlineData("password")][InlineData("owner")][InlineData("private")][InlineData("duplicate")][InlineData("nestedDuplicate")]
    [InlineData("case")][InlineData("numeric")][InlineData("fraction")][InlineData("exponent")][InlineData("guid")][InlineData("missing")]
    [InlineData("null")][InlineData("unsupported")][InlineData("badPoint")][InlineData("noPassword")][InlineData("extraPassword")][InlineData("offset")]
    public async Task GivenMalformedProfileSchema_WhenCreating_ThenRejectWithoutMutation(string invalid)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(client);Authorize(RegistrationApiFixture.Token(s.Actor));var input=Input(s,client,scoped);var text=Encoding.UTF8.GetString(Bytes(input));
        text=invalid switch
        {
            "name"=>text.Insert(1,"\"name\":\"plaintext\","),"password"=>text.Insert(1,"\"password\":\"secret\","),"owner"=>text.Insert(1,"\"accountId\":\""+s.AccountId+"\","),
            "private"=>text.Replace("\"crv\":","\"d\":\"private\",\"crv\":"),"duplicate"=>text.Insert(1,"\"profileId\":\""+input.ProfileId+"\","),
            "nestedDuplicate"=>text.Replace("\"crv\":","\"crv\":\"P-256\",\"crv\":"),"case"=>text.Replace("\"profileId\":","\"ProfileId\":"),
            "numeric"=>text.Replace("\"keyEpoch\":1","\"keyEpoch\":\"1\""),"fraction"=>text.Replace("\"keyEpoch\":1","\"keyEpoch\":1.0"),
            "exponent"=>text.Replace("\"keyEpoch\":1","\"keyEpoch\":1e0"),"guid"=>text.Replace(input.ProfileId.ToString(),input.ProfileId.ToString().ToUpperInvariant()),
            "missing"=>text.Replace("\"recordIds\":[]", "\"otherIds\":[]"),"null"=>text.Replace("\"recordIds\":[]","\"recordIds\":null"),
            "unsupported"=>text.Replace("cerberus-content-v1","unsupported"),"badPoint"=>text.Replace(input.KeyWrappers.MasterKeyWrapper.Enc,"AQID"),
            "noPassword"=>text.Replace("\"unlockMode\":\"Master\"","\"unlockMode\":\"PerProfile\""),
            "extraPassword"=>text.Replace("\"passwordWrapper\":null","\"passwordWrapper\":"+JsonSerializer.Serialize(scoped.Material.PasswordWrapper,ProtectionFixture.Json)),
            _=>text.Replace(JsonSerializer.Serialize(input.EditedAt),JsonSerializer.Serialize(input.EditedAt.ToOffset(TimeSpan.FromHours(1))))
        };
        var before=await Snapshot(s);using var response=await Send(Encoding.UTF8.GetBytes(text),s.Access);await Failure(response,400);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory]
    [InlineData("missingAccess",401)][InlineData("badAccess",400)][InlineData("multipleAccess",400)][InlineData("query",400)]
    [InlineData("gzip",400)][InlineData("bom",400)][InlineData("utf16",400)][InlineData("unicode",400)][InlineData("emptyBody",400)]
    public async Task GivenInvalidWireRequest_WhenCreating_ThenRejectWithoutMutation(string invalid,int status)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(client);Authorize(RegistrationApiFixture.Token(s.Actor));var raw=Bytes(Input(s,client,scoped));
        if(invalid=="bom")raw=[239,187,191,..raw];if(invalid=="unicode")raw=[123,34,120,34,58,34,255,34,125];if(invalid=="emptyBody")raw=[];
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/profiles"+(invalid=="query"?"?owner=untrusted":"")){Content=new ByteArrayContent(raw)};
        request.Content.Headers.ContentType=new("application/json");if(invalid=="utf16")request.Content.Headers.ContentType.CharSet="utf-16";if(invalid=="gzip")request.Content.Headers.ContentEncoding.Add("gzip");
        if(invalid!="missingAccess")request.Headers.Add("X-Cerberus-Vault-Access",invalid=="badAccess"?"bad":s.Access);if(invalid=="multipleAccess")request.Headers.Add("X-Cerberus-Vault-Access",s.Access);
        var before=await Snapshot(s);using var response=await Gateway.Client.SendAsync(request);await Failure(response,status);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory]
    [InlineData("anonymous",401)][InlineData("deletedIdentity",401)][InlineData("providerUnavailable",503)][InlineData("wrongScope",401)]
    [InlineData("missingOwner",404)][InlineData("closing",404)][InlineData("erased",404)][InlineData("noProtection",404)]
    [InlineData("revoked",403)][InlineData("profile",403)][InlineData("expired",403)][InlineData("policy",403)][InlineData("generation",403)]
    public async Task GivenMissingCurrentIdentityOwnerOrPermission_WhenCreating_ThenFailClosed(string invalid,int status)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(client,invalid!="noProtection");
        if(invalid!="anonymous")Authorize(RegistrationApiFixture.Token(invalid=="missingOwner"?Guid.NewGuid():s.Actor));
        if(invalid is "deletedIdentity" or "providerUnavailable")
        {var pair=fixture.Users.Single(x=>x.Value.Id==s.Actor);fixture.Users[pair.Key]=invalid=="deletedIdentity"?pair.Value with {Deleted=true}:pair.Value with {IdentityUnavailable=true};}
        if(invalid=="wrongScope")Authorize(RegistrationApiFixture.Token(s.Actor,Guid.NewGuid()));
        if(invalid=="missingOwner"){var actor=Guid.NewGuid();fixture.Users[Guid.NewGuid()+"@example.test"]=new(actor,"fixture-password");Authorize(RegistrationApiFixture.Token(actor));}
        await using(var db=fixture.Context())
        {
            var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.AccountId==s.InternalId);
            if(invalid=="closing")a.State=AccountState.ClosurePending;if(invalid=="erased")db.TerminalErasures.Add(new(){ResourceId=s.AccountId,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow});
            if(invalid=="revoked")v.Revoked=true;if(invalid=="profile")v.ProfileId=123;if(invalid=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);
            if(invalid=="policy")v.PolicyRevision=2;if(invalid=="generation")v.RevocationGeneration=2;await db.SaveChangesAsync();
        }
        var before=await Snapshot(s);using var response=await Send(Bytes(Input(s,client,scoped)),s.Access);await Failure(response,status);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("records")][InlineData("folders")][InlineData("collections")]
    public async Task GivenNonexistentRelationship_WhenCreating_ThenReturn404(string kind)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(client);Authorize(RegistrationApiFixture.Token(s.Actor));var i=Input(s,client,scoped);
        i=kind switch {"records"=>i with {RecordIds=[Guid.NewGuid()]},"folders"=>i with {FolderIds=[Guid.NewGuid()]},_=>i with {CollectionIds=[Guid.NewGuid()]}};
        var before=await Snapshot(s);using var response=await Send(Bytes(i),s.Access);await Failure(response,404);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenConcurrentDuplicateCreates_WhenPosting_ThenOne201AndOne409()
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(client);Authorize(RegistrationApiFixture.Token(s.Actor));var raw=Bytes(Input(s,client,scoped));
        var responses=await Task.WhenAll(Send(raw,s.Access),Send(raw,s.Access));try{Assert.Single(responses,x=>x.StatusCode==HttpStatusCode.Created);await Failure(Assert.Single(responses,x=>x.StatusCode!=HttpStatusCode.Created),409);}finally{foreach(var response in responses)response.Dispose();}
        await using var db=fixture.Context();Assert.Single(await db.Profiles.Where(x=>x.AccountId==s.InternalId).ToListAsync());
    }
    [FunctionalFact]
    public async Task GivenUnavailableProfileTable_WhenCreating_ThenReturn503Unchanged()
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(client);Authorize(RegistrationApiFixture.Token(s.Actor));var input=Input(s,client,scoped);var before=await Snapshot(s);
        await using var db=fixture.Context();await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.profile RENAME TO fixture_unavailable_profile");
        try{using var response=await Send(Bytes(input),s.Access);await Failure(response,503);}finally{await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_profile RENAME TO profile");}
        Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenNativeProfileAndAccountRotation_WhenPostingThenRotating_ThenRequireAndReplaceProfileAtomically()
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(client);Authorize(RegistrationApiFixture.Token(s.Actor));var input=Input(s,client,scoped,"PerProfile");
        using(var created=await Send(Bytes(input),s.Access))Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        var c=new ProtectionChange(s.AccountId,1,1,"rotate-content",client.Rewrap(),[new("account",s.AccountId,1,Envelope(2)),new("profile",input.ProfileId,1,Envelope(2),input.KeyWrappers with {MasterKeyWrapper=client.Wrap(s.AccountId,"profile",input.ProfileId,s.Actor,2,2),PasswordWrapper=scoped.Rewrap().PasswordWrapper})]);
        var incomplete=c with {ContentReplacements=[c.ContentReplacements[0]]};var raw=Bytes(incomplete);var challenge=await Challenge(raw,"change-protection");var before=await Snapshot(s);
        using(var denied=await Rotate(raw,challenge,client,s.Access))await Failure(denied,400);Assert.Equal(before,await Snapshot(s));
        raw=Bytes(c);challenge=await Challenge(raw,"change-protection");using(var rotated=await Rotate(raw,challenge,client,s.Access))Assert.Equal(HttpStatusCode.OK,rotated.StatusCode);
        await using var db=fixture.Context();var p=await db.Profiles.SingleAsync(x=>x.PublicId==input.ProfileId);Assert.Equal(2,p.Revision);Assert.Equal(c.ContentReplacements[1].KeyWrappers,JsonSerializer.Deserialize<ProfileKeyWrappers>(p.KeyWrappers,ProtectionFixture.Json));
        s=s with {Access=await Unlock(client,2)};using var next=await Send(Bytes(Input(s,client,scoped)),s.Access);Assert.Equal(HttpStatusCode.Created,next.StatusCode);
    }
    private sealed record State(Guid Actor,Guid AccountId,long InternalId,string Access);
    private async Task<State> Setup(ProtectionFixture client,bool initialize=true,bool session=true)
    {
        var actor=Guid.NewGuid();fixture.Users[Guid.NewGuid()+"@example.test"]=new(actor,"fixture-password");await using var db=fixture.Context();var a=new Account{PublicId=Guid.NewGuid(),HeimdallPublicId=actor,DetailsEnvelope=Bytes(Envelope())};db.Accounts.Add(a);await db.SaveChangesAsync();
        if(initialize)db.VaultProtections.Add(new(){AccountId=a.Id,Material=Bytes(client.Material)});var access=ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32));OpaqueAccessHandle.TryHash(access,out var hash);
        if(session)db.VaultAccessSessions.Add(new(){AccountId=a.Id,HandleVerifier=hash,IssuedAt=DateTimeOffset.UtcNow.AddSeconds(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});
        await db.SaveChangesAsync();return new(actor,a.PublicId,a.Id,access);
    }
    private static ProfileCreateInput Input(State s,ProtectionFixture client,ProtectionFixture scoped,string mode="Master")
    {var id=Guid.NewGuid();return new(id,Envelope(),new(mode,scoped.Material.UnlockVerifier,client.Wrap(s.AccountId,"profile",id,s.Actor),mode=="PerProfile"?scoped.Material.PasswordWrapper:null),DateTimeOffset.UtcNow,[],[],[]);}
    private static EncryptedEnvelope Envelope(long epoch=1)=>new("cerberus-content-v1",epoch,ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32)),ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(12)),"AQID",ProtectionFixture.Encode(new byte[16]));
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    private async Task<HttpResponseMessage> Send(byte[] raw,string access)
    {using var request=new HttpRequestMessage(HttpMethod.Post,"/api/profiles"){Content=new ByteArrayContent(raw)};request.Content.Headers.ContentType=new("application/json");request.Headers.Add("X-Cerberus-Vault-Access",access);return await Gateway.Client.SendAsync(request);}
    private async Task<VaultProofChallenge> Challenge(byte[] raw,string operation)
    {using var response=await Gateway.Client.PostAsJsonAsync("/api/vault/challenges",new{operation,requestHash=ProtocolBinary.Encode(SHA256.HashData(raw))});Assert.Equal(HttpStatusCode.Created,response.StatusCode);return(await response.Content.ReadFromJsonAsync<DataOutput<VaultChallengeOutput>>())!.Data!.Challenge;}
    private async Task<string> Unlock(ProtectionFixture client,long revision)
    {
        var raw=Bytes(new{expectedProtectionRevision=revision});var c=await Challenge(raw,"unlock-account");using var request=new HttpRequestMessage(HttpMethod.Post,"/api/vault/unlock"){Content=new ByteArrayContent(raw)};request.Content.Headers.ContentType=new("application/json");request.Headers.Add("X-Cerberus-Challenge-Id",c.ChallengeId.ToString());request.Headers.Add("X-Cerberus-Proof",client.Sign(c));using var response=await Gateway.Client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);return(await response.Content.ReadFromJsonAsync<DataOutput<VaultUnlockOutput>>())!.Data!.VaultAccess;
    }
    private async Task<HttpResponseMessage> Rotate(byte[] raw,VaultProofChallenge c,ProtectionFixture client,string access)
    {using var request=new HttpRequestMessage(HttpMethod.Put,"/api/vault/protection"){Content=new ByteArrayContent(raw)};request.Content.Headers.ContentType=new("application/json");request.Headers.Add("X-Cerberus-Challenge-Id",c.ChallengeId.ToString());request.Headers.Add("X-Cerberus-Proof",client.Sign(c));request.Headers.Add("X-Cerberus-Vault-Access",access);return await Gateway.Client.SendAsync(request);}
    private async Task<string> Snapshot(State s)
    {await using var db=fixture.Context();return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Protection=await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==s.InternalId),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync()});}
    private static async Task Failure(HttpResponseMessage response,int status)
    {Assert.Equal((HttpStatusCode)status,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.False(doc.RootElement.GetProperty("success").GetBoolean());if(doc.RootElement.TryGetProperty("data",out var data))Assert.Equal(JsonValueKind.Null,data.ValueKind);}
}
