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
public class TrashListHttpTests(RegistrationApiFixture fixture):WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenEmptyOwnedTrash_WhenListing_ThenEmptyNoStorePage()
    {using var keys=new ProtectionFixture();var s=await Setup(keys);var before=await Snapshot(s);using var response=await Send(s);var data=await Success(response);Assert.Empty(data.GetProperty("items").EnumerateArray());Assert.Equal(JsonValueKind.Null,data.GetProperty("nextCursor").ValueKind);Assert.Equal(before,await Snapshot(s));}

    [FunctionalFact]
    public async Task GivenFourHttpDeletionKinds_WhenListing_ThenExactNativeRootContractsAndOriginalDeadlines()
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(keys);
        var expected=new Dictionary<string,(Guid Id,JsonElement Deleted)>();
        foreach(var kind in new[]{"record","folder","collection","profile"}){
            var id=Guid.NewGuid();var env=Envelope();object input=kind switch{
                "record"=>new{recordId=id,envelope=env,editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),folderId=(Guid?)null},
                "folder"=>new{folderId=id,envelope=env,editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),parentFolderId=(Guid?)null},
                "collection"=>new{collectionId=id,envelope=env,editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),recordIds=Array.Empty<Guid>(),folderIds=Array.Empty<Guid>()},
                _=>new ProfileCreateInput(id,env,new("Master",scoped.Material.UnlockVerifier,keys.Wrap(s.AccountId,"profile",id,s.Actor),null),DateTimeOffset.UnixEpoch,[],[],[])};
            var route="/api/"+(kind=="collection"?"collections":kind=="profile"?"profiles":kind+"s");
            using(var created=await Send(s,route,HttpMethod.Post,Bytes(input)))Assert.Equal(HttpStatusCode.Created,created.StatusCode);
            using var deleted=await Send(s,route+"/"+id,HttpMethod.Delete,Bytes(new{expectedRevision=1}));var deletion=await Success(deleted);
            expected.Add(kind,(id,deletion));}
        var before=await Snapshot(s);using var response=await Send(s);var payload=await Success(response);Fields(payload,"items","nextCursor");
        var items=payload.GetProperty("items").EnumerateArray().ToArray();Assert.Equal(4,items.Length);
        foreach(var item in items){var kind=item.GetProperty("kind").GetString()!;var e=expected[kind];
            Assert.Equal(e.Id,item.GetProperty("resourceId").GetGuid());Assert.Equal(e.Deleted.GetProperty("trashOperationId").GetGuid(),item.GetProperty("operationId").GetGuid());
            Assert.Equal(e.Deleted.GetProperty("deletedAt").GetDateTimeOffset(),item.GetProperty("deletedAt").GetDateTimeOffset());Assert.Equal(e.Deleted.GetProperty("purgeAt").GetDateTimeOffset(),item.GetProperty("purgeAt").GetDateTimeOffset());
            var names=new[]{"kind","operationId","resourceId","revision","serverSequence","editedAt","deletedAt","purgeAt","envelope"};
            Fields(item,kind=="profile"?[..names,"keyWrappers"]:kind=="collection"?[..names,"keyEpoch"]:names);
            Assert.Equal("cerberus-content-v1",item.GetProperty("envelope").GetProperty("format").GetString());
            if(Environment.GetEnvironmentVariable("CERBERUS_CAPTURE_UC50_JSON")=="1")await File.WriteAllTextAsync("/tmp/cerberus-uc50-http-"+kind+".json",item.GetRawText());}
        Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenNativeSelectedHandle_WhenListingTrash_ThenRetainedOwnProfileScopeOnly(string mode)
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(keys);var profileId=Guid.NewGuid();
        var input=new ProfileCreateInput(profileId,Envelope(),new(mode,scoped.Material.UnlockVerifier,keys.Wrap(s.AccountId,"profile",profileId,s.Actor),mode=="PerProfile"?scoped.Material.PasswordWrapper:null),DateTimeOffset.UnixEpoch,[],[],[]);
        using(var created=await Send(s,"/api/profiles",HttpMethod.Post,Bytes(input)))Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        var hidden=await CreateTrashRecord(s,[]);var visible=await CreateTrashRecord(s,[profileId]);
        await using var db=fixture.Context();var profile=await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==profileId);
        var selected=s with{Access=await Open(s,profile,scoped)};var before=await Snapshot(s);using var response=await Send(selected);var data=await Success(response);
        var item=Assert.Single(data.GetProperty("items").EnumerateArray());Assert.Equal(visible,item.GetProperty("resourceId").GetGuid());Assert.NotEqual(hidden,item.GetProperty("resourceId").GetGuid());Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenTwoOperationsAndLaterDeletion_WhenPaging_ThenOpaqueBoundAndNoNewOperation()
    {
        using var keys=new ProtectionFixture();var s=await Setup(keys);var first=await CreateTrashRecord(s,[]);var second=await CreateTrashRecord(s,[]);
        using var r=await Send(s,"/api/trash?pageSize=1");var initial=await Success(r);Assert.Equal(first,Assert.Single(initial.GetProperty("items").EnumerateArray()).GetProperty("resourceId").GetGuid());
        var cursor=initial.GetProperty("nextCursor").GetString()!;Assert.DoesNotContain(s.Actor.ToString(),cursor);await CreateTrashRecord(s,[]);
        using var next=await Send(s,"/api/trash?pageSize=1&cursor="+cursor);var end=await Success(next);Assert.Equal(second,Assert.Single(end.GetProperty("items").EnumerateArray()).GetProperty("resourceId").GetGuid());Assert.Equal(JsonValueKind.Null,end.GetProperty("nextCursor").ValueKind);
    }
    [FunctionalTheory][InlineData("?pageSize=0")][InlineData("?pageSize=-1")][InlineData("?pageSize=101")][InlineData("?pageSize=2147483648")]
    [InlineData("?pageSize=01")][InlineData("?pageSize=+1")][InlineData("?pageSize=%201")][InlineData("?pageSize=1.0")][InlineData("?pageSize=")]
    [InlineData("?pageSize=1&pageSize=2")][InlineData("?PageSize=1")][InlineData("?ownerId=private")]
    [InlineData("?cursor=")][InlineData("?cursor=bad")][InlineData("?cursor=a&cursor=b")]
    public async Task GivenMalformedQuery_WhenListing_ThenStable400WithoutMutation(string query)
    {using var keys=new ProtectionFixture();var s=await Setup(keys);var before=await Snapshot(s);using var response=await Send(s,"/api/trash"+query);await Failure(response,400,"validation_failed");Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("missingAccess",401)][InlineData("badAccess",400)][InlineData("duplicateAccess",400)]
    [InlineData("body",400)][InlineData("oversized",413)][InlineData("noBearerOversized",413)][InlineData("authority",400)]
    public async Task GivenInvalidTransport_WhenListing_ThenRejectBeforeDisclosure(string kind,int status)
    {
        using var keys=new ProtectionFixture();var s=await Setup(keys);var before=await Snapshot(s);using var request=new HttpRequestMessage(HttpMethod.Get,"/api/trash");
        if(kind!="noBearerOversized")request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));
        if(kind!="missingAccess")request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",kind=="badAccess"?"bad":s.Access);
        if(kind=="duplicateAccess")request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",s.Access);
        if(kind=="authority")request.Headers.TryAddWithoutValidation("X-Cerberus-Owner",Guid.NewGuid().ToString());
        if(kind is "body" or "oversized" or "noBearerOversized")request.Content=new StringContent(kind=="body"?"{}":new string(' ',65537));
        using var response=await Gateway.Client.SendAsync(request);await Failure(response,status);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("empty")][InlineData("http2")]
    public async Task GivenRawEmptyHandleOrUnknownLengthBody_WhenListing_Then400(string kind)
    {
        using var keys=new ProtectionFixture();var s=await Setup(keys);using var host=new WebApplicationFactory<Program>();
        var context=await host.Server.SendAsync(c=>{c.Request.Method="GET";c.Request.Path="/api/trash";c.Request.Headers.Authorization="Bearer "+RegistrationApiFixture.Token(s.Actor);c.Request.Headers["X-Cerberus-Vault-Access"]=kind=="empty"?"":s.Access;if(kind=="http2")c.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature());});
        Assert.Equal(400,context.Response.StatusCode);Assert.Contains("no-store",context.Response.Headers.CacheControl.ToString());
    }
    [FunctionalTheory][InlineData("missing",401)][InlineData("wrongScope",401)][InlineData("deleted",401)][InlineData("unavailable",503)]
    public async Task GivenInvalidCurrentIdentity_WhenListing_ThenFailClosed(string kind,int status)
    {
        using var keys=new ProtectionFixture();var s=await Setup(keys);await CreateTrashRecord(s,[]);var before=await Snapshot(s);
        if(kind is "deleted" or "unavailable")fixture.Users[s.Actor+"@example.test"]=new(s.Actor,"fixture-password",Deleted:kind=="deleted",IdentityUnavailable:kind=="unavailable");
        using var request=new HttpRequestMessage(HttpMethod.Get,"/api/trash");request.Headers.Add("X-Cerberus-Vault-Access",s.Access);
        if(kind!="missing")request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor,kind=="wrongScope"?Guid.NewGuid():null));
        using var response=await Gateway.Client.SendAsync(request);await Failure(response,status);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("envelope")][InlineData("revision")][InlineData("sequence")][InlineData("deadline")]
    public async Task GivenVisibleCorruption_WhenListing_Then503WithoutPartialPage(string kind)
    {
        using var keys=new ProtectionFixture();var s=await Setup(keys);await CreateTrashRecord(s,[]);var target=await CreateTrashRecord(s,[]);
        await using(var db=fixture.Context()){
            if(kind=="envelope")await db.Records.Where(x=>x.PublicId==target).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,"bad"u8.ToArray()));
            if(kind=="revision")await db.Records.Where(x=>x.PublicId==target).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Revision,0));
            if(kind=="sequence")await db.Records.Where(x=>x.PublicId==target).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.ServerSequence,0));
            if(kind=="deadline")await db.Records.Where(x=>x.PublicId==target).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.PurgeAt,DateTimeOffset.UtcNow.AddDays(31)));}
        var before=await Snapshot(s);using var response=await Send(s);await Failure(response,503,"persistence_unavailable");Assert.Equal(before,await Snapshot(s));
    }
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
