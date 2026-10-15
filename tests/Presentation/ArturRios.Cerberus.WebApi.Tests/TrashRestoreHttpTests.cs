using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Configuration.Enums;
using ArturRios.Util.Test.Functional;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public partial class TrashRestoreHttpTests(RegistrationApiFixture fixture):WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenActualHttpTrash_WhenRestoring_ThenMetadataOnlyAndExactNativeBytesRemain(string kind)
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(keys);var id=Guid.NewGuid();var env=Envelope();object input=kind switch{
            "record"=>new{recordId=id,envelope=env,editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),folderId=(Guid?)null},
            "folder"=>new{folderId=id,envelope=env,editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),parentFolderId=(Guid?)null},
            "collection"=>new{collectionId=id,envelope=env,editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),recordIds=Array.Empty<Guid>(),folderIds=Array.Empty<Guid>()},
            _=>new ProfileCreateInput(id,env,new("Master",scoped.Material.UnlockVerifier,keys.Wrap(s.AccountId,"profile",id,s.Actor),null),DateTimeOffset.UnixEpoch,[],[],[])};
        var route="/api/"+(kind=="collection"?"collections":kind=="profile"?"profiles":kind+"s");
        using(var r=await Send(s,route,HttpMethod.Post,Bytes(input)))Assert.Equal(HttpStatusCode.Created,r.StatusCode);
        using var deleted=await Send(s,route+"/"+id,HttpMethod.Delete,Bytes(new{expectedRevision=1}));var trash=await Success(deleted);var op=trash.GetProperty("trashOperationId").GetGuid();
        using var restored=await Send(s,"/api/trash/"+op+"/restore",HttpMethod.Post,Bytes(new{expectedRevision=trash.GetProperty("revision").GetInt64()}));var data=await Success(restored);
        Fields(data,"operationId","rootKind","rootResourceId","restoredAt","items","omissions");Assert.Equal(op,data.GetProperty("operationId").GetGuid());Assert.Equal(id,data.GetProperty("rootResourceId").GetGuid());Assert.Equal(kind,data.GetProperty("rootKind").GetString());Assert.Empty(data.GetProperty("omissions").EnumerateArray());
        var item=Assert.Single(data.GetProperty("items").EnumerateArray());Fields(item,"kind","resourceId","revision","serverSequence","editedAt");Assert.Equal(3,item.GetProperty("revision").GetInt64());Assert.Equal(DateTimeOffset.UnixEpoch,item.GetProperty("editedAt").GetDateTimeOffset());Assert.True(item.GetProperty("serverSequence").GetInt64()>trash.GetProperty("serverSequence").GetInt64());
        using var read=await Send(s,route+"/"+id);var current=await Success(read);Assert.Equal(Bytes(env),Bytes(current.GetProperty("envelope").Deserialize<EncryptedEnvelope>(ProtectionFixture.Json)!));
        using var list=await Send(s);Assert.Empty((await Success(list)).GetProperty("items").EnumerateArray());
        using var retry=await Send(s,"/api/trash/"+op+"/restore",HttpMethod.Post,Bytes(new{expectedRevision=2}));await Failure(retry,404,"not_found");
        if(Environment.GetEnvironmentVariable("CERBERUS_CAPTURE_UC51_JSON")=="1")await File.WriteAllTextAsync("/tmp/cerberus-uc51-http-"+kind+".json",data.GetRawText());
    }
    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenCurrentNativeSelectedProfile_WhenRestoring_ThenOriginalAuthorizedRecordVisibleAndOtherTrashHidden(string mode)
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(keys);var id=Guid.NewGuid();
        var input=new ProfileCreateInput(id,Envelope(),new(mode,scoped.Material.UnlockVerifier,keys.Wrap(s.AccountId,"profile",id,s.Actor),mode=="PerProfile"?scoped.Material.PasswordWrapper:null),DateTimeOffset.UnixEpoch,[],[],[]);
        using(var r=await Send(s,"/api/profiles",HttpMethod.Post,Bytes(input)))Assert.Equal(HttpStatusCode.Created,r.StatusCode);
        var hidden=await CreateTrashRecord(s,[]);var visible=await CreateTrashRecord(s,[id]);await using var db=fixture.Context();var p=await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==id);var selected=s with{Access=await Open(s,p,scoped)};
        var op=await db.TrashOperations.AsNoTracking().SingleAsync(x=>x.RootResourceId==visible&&x.RootResourceKind=="record");var other=await db.TrashOperations.AsNoTracking().SingleAsync(x=>x.RootResourceId==hidden&&x.RootResourceKind=="record");
        using(var r=await Send(selected,"/api/trash/"+other.PublicId+"/restore",HttpMethod.Post,Bytes(new{expectedRevision=99})))await Failure(r,404,"not_found");
        using(var r=await Send(selected,"/api/trash/"+op.PublicId+"/restore",HttpMethod.Post,Bytes(new{expectedRevision=2})))await Success(r);
        using var read=await Send(selected,"/api/records/"+visible);await Success(read);Assert.Equal(p.Id,(await db.VaultAccessSessions.AsNoTracking().SingleAsync(x=>x.HandleVerifier==Hash(selected.Access))).ProfileId);
    }
    [FunctionalTheory][InlineData("{}")][InlineData("{\"expectedRevision\":0}")][InlineData("{\"expectedRevision\":-1}")]
    [InlineData("{\"expectedRevision\":9007199254740992}")][InlineData("{\"expectedRevision\":\"2\"}")][InlineData("{\"expectedRevision\":2.0}")]
    [InlineData("{\"expectedRevision\":2,\"expectedRevision\":2}")][InlineData("{\"ExpectedRevision\":2}")][InlineData("{\"expectedRevision\":2,\"ownerId\":\"x\"}")]
    [InlineData("{\"expectedRevision\":null}")][InlineData("null")][InlineData("[]")][InlineData("bad")][InlineData("")]
    public async Task GivenMalformedRestoreBody_WhenPosting_ThenStable400AndNoMutation(string raw)
    {using var keys=new ProtectionFixture();var s=await Setup(keys);var id=await CreateTrashRecord(s,[]);await using var db=fixture.Context();var op=await db.TrashOperations.SingleAsync(x=>x.RootResourceId==id);var before=await Snapshot(s);using var r=await Send(s,"/api/trash/"+op.PublicId+"/restore",HttpMethod.Post,System.Text.Encoding.UTF8.GetBytes(raw));await Failure(r,400,"validation_failed");Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("badId",400)][InlineData("upperId",400)][InlineData("zeroId",400)][InlineData("query",400)]
    [InlineData("missingAccess",401)][InlineData("badAccess",400)][InlineData("duplicateAccess",400)]
    [InlineData("authority",400)][InlineData("media",400)][InlineData("oversized",413)][InlineData("noBearerOversized",413)]
    [InlineData("noBearer",401)][InlineData("wrongScope",401)][InlineData("deleted",401)][InlineData("unavailable",503)]
    public async Task GivenInvalidRestoreTransportOrIdentity_WhenPosting_ThenRejectWithoutMutation(string kind,int status)
    {
        using var keys=new ProtectionFixture();var s=await Setup(keys);var id=await CreateTrashRecord(s,[]);await using var db=fixture.Context();var op=await db.TrashOperations.SingleAsync(x=>x.RootResourceId==id);var before=await Snapshot(s);
        var route=kind=="badId"?"bad":kind=="upperId"?op.PublicId.ToString().ToUpperInvariant():kind=="zeroId"?Guid.Empty.ToString():op.PublicId.ToString();
        using var request=Request(s,"/api/trash/"+route+"/restore"+(kind=="query"?"?ownerId=x":""),HttpMethod.Post,kind is "oversized" or "noBearerOversized"?new byte[65537]:Bytes(new{expectedRevision=2}));
        if(kind is "missingAccess" or "badAccess" or "emptyAccess"){request.Headers.Remove("X-Cerberus-Vault-Access");if(kind!="missingAccess")request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",kind=="badAccess"?"bad":"");}
        if(kind=="duplicateAccess")request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",s.Access);if(kind=="authority")request.Headers.Add("X-Cerberus-Owner",Guid.NewGuid().ToString());
        if(kind=="media")request.Content!.Headers.ContentType=new("text/plain");if(kind is "noBearer" or "noBearerOversized")request.Headers.Authorization=null;if(kind=="wrongScope")request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor,Guid.NewGuid()));
        if(kind is "deleted" or "unavailable")fixture.Users[s.Actor+"@example.test"]=new(s.Actor,"fixture-password",Deleted:kind=="deleted",IdentityUnavailable:kind=="unavailable");
        using var response=await Gateway.Client.SendAsync(request);await Failure(response,status);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("stale",409,"revision_conflict")][InlineData("expired",410,"trash_expired")]
    [InlineData("terminal",404,"not_found")][InlineData("corrupt",503,"persistence_unavailable")][InlineData("revoked",403,"vault_access_denied")]
    public async Task GivenChangedRestoreAuthorityOrState_WhenPosting_ThenSafeFailureAndNoPartialWrite(string kind,int status,string error)
    {
        using var keys=new ProtectionFixture();var s=await Setup(keys);var id=await CreateTrashRecord(s,[]);await using var db=fixture.Context();var op=await db.TrashOperations.SingleAsync(x=>x.RootResourceId==id);
        if(kind=="expired"){var deadline=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());await db.TrashOperations.Where(x=>x.Id==op.Id).ExecuteUpdateAsync(x=>x.SetProperty(o=>o.PurgeAt,deadline).SetProperty(o=>o.DeletedAt,deadline.AddDays(-30)));await db.Records.Where(x=>x.PublicId==id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.PurgeAt,deadline).SetProperty(r=>r.DeletedAt,deadline.AddDays(-30)));}
        if(kind=="terminal"){db.TerminalErasures.Add(new(){ResourceKind="record",ResourceId=id,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        if(kind=="corrupt")await db.Records.Where(x=>x.PublicId==id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,"bad"u8.ToArray()));
        if(kind=="revoked")await db.VaultAccessSessions.Where(x=>x.HandleVerifier==Hash(s.Access)).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Revoked,true));
        var before=await Snapshot(s);using var response=await Send(s,"/api/trash/"+op.PublicId+"/restore",HttpMethod.Post,Bytes(new{expectedRevision=kind=="stale"?99:2}));await Failure(response,status,error);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenRawPresentEmptyAccessHeader_WhenRestoring_Then400()
    {
        using var keys=new ProtectionFixture();var s=await Setup(keys);var before=await Snapshot(s);using var host=new WebApplicationFactory<Program>();
        var bytes=Bytes(new{expectedRevision=2});
        var context=await host.Server.SendAsync(c=>{c.Request.Method="POST";c.Request.Path="/api/trash/"+Guid.NewGuid()+"/restore";c.Request.ContentType="application/json";c.Request.ContentLength=bytes.Length;c.Request.Body=new MemoryStream(bytes);c.Request.Headers.Authorization="Bearer "+RegistrationApiFixture.Token(s.Actor);c.Request.Headers["X-Cerberus-Vault-Access"]="";});
        Assert.Equal(400,context.Response.StatusCode);Assert.Contains("no-store",context.Response.Headers.CacheControl.ToString());Assert.Equal(before,await Snapshot(s));
    }
    private static string Hash(string access){Assert.True(OpaqueAccessHandle.TryHash(access,out var verifier));return verifier!;}
    private sealed class BodyFeature:IHttpRequestBodyDetectionFeature{public bool CanHaveBody=>true;}
    private sealed record State(Guid Actor,Guid AccountId,long Id,string Access);
    private async Task<State> Setup(ProtectionFixture keys)
    {
        var actor=Guid.NewGuid();fixture.Users[actor+"@example.test"]=new(actor,"fixture-password");await using var db=fixture.Context();
        var account=new Account{PublicId=Guid.NewGuid(),HeimdallPublicId=actor,DetailsEnvelope=Bytes(Envelope())};db.Accounts.Add(account);await db.SaveChangesAsync();
        var access=ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32));OpaqueAccessHandle.TryHash(access,out var verifier);
        db.VaultProtections.Add(new(){AccountId=account.Id,Material=Bytes(keys.Material)});db.VaultAccessSessions.Add(new(){AccountId=account.Id,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();return new(actor,account.PublicId,account.Id,access);
    }
    private async Task<Guid> CreateTrashRecord(State s,Guid[] profiles)
    {var id=Guid.NewGuid();using(var created=await Send(s,"/api/records",HttpMethod.Post,Bytes(new{recordId=id,envelope=Envelope(),editedAt=DateTimeOffset.UnixEpoch,profileIds=profiles,folderId=(Guid?)null})))Assert.Equal(HttpStatusCode.Created,created.StatusCode);using var deleted=await Send(s,"/api/records/"+id,HttpMethod.Delete,Bytes(new{expectedRevision=1}));await Success(deleted);return id;}
    private async Task<string> Open(State s,Profile p,ProtectionFixture scoped)
    {
        var raw=Bytes(new{expectedRevision=p.Revision});using var challengeRequest=Request(s,"/api/profiles/"+p.PublicId+"/access/challenges",HttpMethod.Post,Bytes(new{expectedRevision=p.Revision,requestHash=ProtocolBinary.Encode(SHA256.HashData(raw))}));challengeRequest.Headers.Remove("X-Cerberus-Vault-Access");
        using var issued=await Gateway.Client.SendAsync(challengeRequest);var issuedData=await Success(issued,201);var challenge=issuedData.GetProperty("challenge").Deserialize<VaultProofChallenge>(ProtectionFixture.Json)!;
        using var open=Request(s,"/api/profiles/"+p.PublicId+"/access",HttpMethod.Post,raw);open.Headers.Remove("X-Cerberus-Vault-Access");open.Headers.Add("X-Cerberus-Challenge-Id",challenge.ChallengeId.ToString());open.Headers.Add("X-Cerberus-Proof",scoped.Sign(challenge));using var response=await Gateway.Client.SendAsync(open);return (await Success(response)).GetProperty("vaultAccess").GetString()!;
    }
    private static EncryptedEnvelope Envelope()=>new("cerberus-content-v1",1,ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)),ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(12)),"AQID",ProtocolBinary.Encode(new byte[16]));
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    private static HttpRequestMessage Request(State s,string path,HttpMethod method,byte[]? body=null){var r=new HttpRequestMessage(method,path);r.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));r.Headers.Add("X-Cerberus-Vault-Access",s.Access);if(body is not null){r.Content=new ByteArrayContent(body);r.Content.Headers.ContentType=new("application/json");}return r;}
    private async Task<HttpResponseMessage> Send(State s,string path="/api/trash",HttpMethod? method=null,byte[]? body=null){using var request=Request(s,path,method??HttpMethod.Get,body);return await Gateway.Client.SendAsync(request);}
    private static async Task<JsonElement> Success(HttpResponseMessage r,int status=200){Assert.Equal((HttpStatusCode)status,r.StatusCode);Assert.True(r.Headers.CacheControl?.NoStore);using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Assert.True(d.RootElement.GetProperty("success").GetBoolean());return d.RootElement.GetProperty("data").Clone();}
    private static async Task Failure(HttpResponseMessage r,int status,string? error=null){Assert.Equal((HttpStatusCode)status,r.StatusCode);Assert.True(r.Headers.CacheControl?.NoStore);if(status==413){Assert.Equal("",await r.Content.ReadAsStringAsync());return;}using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Assert.False(d.RootElement.GetProperty("success").GetBoolean());if(d.RootElement.TryGetProperty("data",out var data))Assert.Equal(JsonValueKind.Null,data.ValueKind);if(error is not null)Assert.Contains(error,d.RootElement.GetProperty("errors").EnumerateArray().Select(x=>x.GetString()));}
    private static void Fields(JsonElement e,params string[] names)=>Assert.Equal(names.Order(),e.EnumerateObject().Select(x=>x.Name).Order());
    private async Task<string> Snapshot(State s){await using var db=fixture.Context();return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.Id),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.Id).ToArrayAsync(),Operations=await db.TrashOperations.AsNoTracking().Where(x=>x.AccountId==s.Id).OrderBy(x=>x.Id).ToArrayAsync(),Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.Id).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.Id).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.Id).OrderBy(x=>x.Id).ToArrayAsync(),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.Id).OrderBy(x=>x.Id).ToArrayAsync()},ProtectionFixture.Json);}
}
