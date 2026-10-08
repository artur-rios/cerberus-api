using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Configuration.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class ProfileTrashHttpTests(RegistrationApiFixture fixture):WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalTheory][InlineData("foreign")][InlineData("trash")][InlineData("erased")][InlineData("missing")]
    public async Task GivenHiddenOrAbsentProfile_WhenDeleting_ThenSameNonrevealing404(string kind)
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var owner=kind=="foreign"?await Setup(f):s;var input=await Add(owner,f,scoped);
        await using(var db=fixture.Context()){if(kind=="trash")await db.Profiles.Where(x=>x.PublicId==input.ProfileId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,DateTimeOffset.UtcNow));if(kind=="erased"){db.TerminalErasures.Add(new(){ResourceId=input.ProfileId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}
        Authorize(RegistrationApiFixture.Token(s.Actor));var before=await Snapshot(owner);using var response=await Send((kind=="missing"?Guid.NewGuid():input.ProfileId).ToString(),s.Access);await Failure(response,404,"not_found");Assert.Equal(before,await Snapshot(owner));}
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenValidSelectedSession_WhenDeleting_ThenSelectionSucceedsAndOtherOwnProfile404(bool otherTarget)
    {
        using var additionalScoped1=new ProtectionFixture();using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var one=await Add(s,f,scoped);var two=await Add(s,f,additionalScoped1);await using(var db=fixture.Context()){var p=await db.Profiles.SingleAsync(x=>x.PublicId==one.ProfileId);await db.VaultAccessSessions.Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ProfileId,p.Id));}
        Authorize(RegistrationApiFixture.Token(s.Actor));var before=await Snapshot(s);using var response=await Send((otherTarget?two.ProfileId:one.ProfileId).ToString(),s.Access);if(otherTarget)await Failure(response,404,"not_found");else {Assert.Equal(one.ProfileId,(await Profile(response)).ProfileId);return;}Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("bad")][InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("ABCDEFAB-1234-4321-ABCD-123456789ABC")][InlineData("abcdefab12344321abcd123456789abc")]
    [InlineData("{abcdefab-1234-4321-abcd-123456789abc}")][InlineData("%20abcdefab-1234-4321-abcd-123456789abc")]
    public async Task GivenMalformedOrNoncanonicalPath_WhenDeleting_Then400(string id)
    {using var f=new ProtectionFixture();var s=await Setup(f);Authorize(RegistrationApiFixture.Token(s.Actor));var before=await Snapshot(s);using var response=await Send(id,s.Access);await Failure(response,400);Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("query",400)][InlineData("duplicate",400)][InlineData("malformed",400)][InlineData("missing",401)]
    public async Task GivenInvalidQueryOrAccess_WhenDeleting_ThenRejectWithoutChanges(string kind,int status)
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var p=await Add(s,f,scoped);Authorize(RegistrationApiFixture.Token(s.Actor));var before=await Snapshot(s);using var request=new HttpRequestMessage(HttpMethod.Delete,"/api/profiles/"+p.ProfileId+(kind=="query"?"?pageSize=1":"")){Content=JsonContent.Create(Input())};if(kind!="missing")request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",kind=="malformed"?"bad":s.Access);if(kind=="duplicate")request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",s.Access);using var response=await Gateway.Client.SendAsync(request);await Failure(response,status);Assert.Equal(before,await Snapshot(s));}
    [FunctionalFact]
    public async Task GivenRawEmptyAccess_WhenDeleting_Then400()
    {using var f=new ProtectionFixture();var s=await Setup(f);using var host=new WebApplicationFactory<Program>();var raw=JsonSerializer.SerializeToUtf8Bytes(Input(),ProtectionFixture.Json);var context=await host.Server.SendAsync(async c=>{c.Request.Method="DELETE";c.Request.Path="/api/profiles/"+Guid.NewGuid();c.Request.Headers.Authorization="Bearer "+RegistrationApiFixture.Token(s.Actor);c.Request.Headers["X-Cerberus-Vault-Access"]="";c.Request.ContentType="application/json";c.Request.ContentLength=raw.Length;c.Request.Body=new MemoryStream(raw);await Task.CompletedTask;});Assert.Equal(400,context.Response.StatusCode);Assert.Contains("no-store",context.Response.Headers.CacheControl.ToString());}
    [FunctionalTheory][InlineData("missing",401)][InlineData("wrongscope",401)][InlineData("deleted",401)][InlineData("unavailable",503)]
    public async Task GivenInvalidOrUnavailableIdentity_WhenDeleting_ThenFailClosed(string kind,int status)
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var p=await Add(s,f,scoped);if(kind!="missing")Authorize(RegistrationApiFixture.Token(s.Actor,kind=="wrongscope"?Guid.NewGuid():null));if(kind is "deleted" or "unavailable"){var pair=fixture.Users.Single(x=>x.Value.Id==s.Actor);fixture.Users[pair.Key]=pair.Value with {Deleted=kind=="deleted",IdentityUnavailable=kind=="unavailable"};}var before=await Snapshot(s);using var response=await Send(p.ProfileId.ToString(),s.Access);await Failure(response,status);Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("missing",404)][InlineData("closing",404)][InlineData("erased",404)]
    [InlineData("revoked",403)][InlineData("expired",403)][InlineData("policy",403)][InlineData("generation",403)][InlineData("selection",403)]
    public async Task GivenInvalidCurrentAccountOrSession_WhenDeleting_ThenDenyUnchanged(string kind,int status)
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var p=await Add(s,f,scoped);var actor=s.Actor;
        await using(var db=fixture.Context()){if(kind=="missing"){actor=Guid.NewGuid();fixture.Users[actor+"@example.test"]=new(actor,"fixture-password");}if(kind=="closing")await db.Accounts.Where(x=>x.Id==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.ClosurePending));if(kind=="erased"){db.TerminalErasures.Add(new(){ResourceId=s.AccountId,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}var v=await db.VaultAccessSessions.SingleAsync(x=>x.AccountId==s.InternalId);if(kind=="revoked")v.Revoked=true;if(kind=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(kind=="policy")v.PolicyRevision++;if(kind=="generation")v.RevocationGeneration++;if(kind=="selection")v.ProfileId=long.MaxValue;await db.SaveChangesAsync();}
        Authorize(RegistrationApiFixture.Token(actor));var before=await Snapshot(s);using var response=await Send(p.ProfileId.ToString(),s.Access);await Failure(response,status);Assert.Equal(before,await Snapshot(s));}
    [FunctionalFact]
    public async Task GivenUnavailableProfileTable_WhenDeleting_Then503Unchanged()
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var p=await Add(s,f,scoped);Authorize(RegistrationApiFixture.Token(s.Actor));var before=await Snapshot(s);await using var db=fixture.Context();await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.profile RENAME TO fixture_unavailable_profile");try{using var response=await Send(p.ProfileId.ToString(),s.Access);await Failure(response,503);}finally{await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_profile RENAME TO profile");}Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenOwnedProfile_WhenDeleting_ThenReturnOnlyTrashMetadataAndRetainRecoverableBytes(string mode)
    {
        using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var i=await Add(s,f,scoped,mode);Authorize(RegistrationApiFixture.Token(s.Actor));Profile before;await using(var db=fixture.Context())before=await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==i.ProfileId);
        using var response=await Send(i.ProfileId.ToString(),s.Access);var data=await Profile(response);Assert.Equal(i.ProfileId,data.ProfileId);Assert.NotEqual(Guid.Empty,data.TrashOperationId);Assert.Equal(2,data.Revision);Assert.True(data.ServerSequence>before.ServerSequence);Assert.Equal(TimeSpan.FromDays(30),data.PurgeAt-data.DeletedAt);
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal(new[]{"deletedAt","profileId","purgeAt","revision","serverSequence","trashOperationId"},doc.RootElement.GetProperty("data").EnumerateObject().Select(x=>x.Name).Order().ToArray());
        await using(var db=fixture.Context()){var row=await db.Profiles.SingleAsync(x=>x.PublicId==i.ProfileId);Assert.Equal(before.Envelope,row.Envelope);Assert.Equal(before.KeyWrappers,row.KeyWrappers);Assert.Equal(before.EditedAt,row.EditedAt);Assert.Equal(data.DeletedAt,row.DeletedAt);Assert.Equal(data.PurgeAt,row.PurgeAt);var op=await db.TrashOperations.SingleAsync(x=>x.PublicId==data.TrashOperationId);Assert.Equal(s.InternalId,op.AccountId);var entry=await db.TrashEntries.SingleAsync(x=>x.OperationId==op.Id);Assert.Equal(i.ProfileId,entry.ResourceId);Assert.Equal(data.PurgeAt,(await db.RetentionWorkItems.SingleAsync(x=>x.OperationKey=="trash/"+data.TrashOperationId)).DueAt);Assert.False((await db.VaultAccessSessions.SingleAsync(x=>x.AccountId==s.InternalId)).Revoked);}
        using var get=await Get("/api/profiles/"+i.ProfileId,s.Access);await Failure(get,404,"not_found");using var list=await Get("/api/profiles",s.Access);Assert.Equal(HttpStatusCode.OK,list.StatusCode);using var listed=JsonDocument.Parse(await list.Content.ReadAsStringAsync());Assert.Empty(listed.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
        var snapshot=await Snapshot(s);using var retry=await Send(i.ProfileId.ToString(),s.Access);await Failure(retry,404,"not_found");Assert.Equal(snapshot,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenSelectedProfileHandle_WhenDeleting_ThenRevokeItAndKeepUnrelatedAndAccountWideHandles()
    {
        using var additionalScoped1=new ProtectionFixture();
        using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var one=await Add(s,f,scoped);var two=await Add(s,f,additionalScoped1);var selected=ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32));var unrelated=ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32));OpaqueAccessHandle.TryHash(selected,out var hash);OpaqueAccessHandle.TryHash(unrelated,out var otherHash);
        await using(var db=fixture.Context()){var p=await db.Profiles.SingleAsync(x=>x.PublicId==one.ProfileId);var p2=await db.Profiles.SingleAsync(x=>x.PublicId==two.ProfileId);foreach(var pair in new[]{(hash,p.Id),(otherHash,p2.Id)})db.VaultAccessSessions.Add(new(){AccountId=s.InternalId,HandleVerifier=pair.Item1,ProfileId=pair.Item2,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();}
        Authorize(RegistrationApiFixture.Token(s.Actor));using var deleted=await Send(one.ProfileId.ToString(),selected);await Profile(deleted);using var denied=await Get("/api/profiles/"+one.ProfileId,selected);await Failure(denied,403,"vault_access_denied");using var permitted=await Get("/api/profiles/"+two.ProfileId,unrelated);Assert.Equal(HttpStatusCode.OK,permitted.StatusCode);using var all=await Get("/api/profiles",s.Access);Assert.Equal(HttpStatusCode.OK,all.StatusCode);using var list=JsonDocument.Parse(await all.Content.ReadAsStringAsync());Assert.Equal(two.ProfileId,Assert.Single(list.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()).GetProperty("profileId").GetGuid());await using var check=fixture.Context();Assert.True((await check.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==hash)).Revoked);Assert.False((await check.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==otherHash)).Revoked);
    }
    [FunctionalTheory][InlineData("unknown")][InlineData("duplicate")][InlineData("numeric")][InlineData("empty")][InlineData("null")][InlineData("revision")][InlineData("negative")][InlineData("unsafe")][InlineData("owner")][InlineData("profile")][InlineData("operation")][InlineData("case")][InlineData("array")]
    public async Task GivenInvalidOrForgedBody_WhenDeleting_Then400WithoutMutation(string kind)
    {
        using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var p=await Add(s,f,scoped);Authorize(RegistrationApiFixture.Token(s.Actor));var raw=kind switch {
            "unknown"=>"{\"expectedRevision\":1,\"extra\":1}","duplicate"=>"{\"expectedRevision\":1,\"expectedRevision\":1}","numeric"=>"{\"expectedRevision\":\"1\"}","empty"=>"{}","null"=>"null","revision"=>"{\"expectedRevision\":0}","negative"=>"{\"expectedRevision\":-1}","unsafe"=>"{\"expectedRevision\":9007199254740992}","owner"=>"{\"expectedRevision\":1,\"actor\":\""+Guid.NewGuid()+"\"}","profile"=>"{\"expectedRevision\":1,\"profileId\":\""+Guid.NewGuid()+"\"}","operation"=>"{\"expectedRevision\":1,\"trashOperationId\":\""+Guid.NewGuid()+"\"}","case"=>"{\"ExpectedRevision\":1}",_=>"[]"};
        var before=await Snapshot(s);using var request=new HttpRequestMessage(HttpMethod.Delete,"/api/profiles/"+p.ProfileId){Content=new StringContent(raw,System.Text.Encoding.UTF8,"application/json")};request.Headers.Add("X-Cerberus-Vault-Access",s.Access);using var response=await Gateway.Client.SendAsync(request);await Failure(response,400);Assert.Equal(before,await Snapshot(s));await using var db=fixture.Context();Assert.False(await db.TrashOperations.AnyAsync(x=>x.AccountId==s.InternalId));
    }
    [FunctionalFact]
    public async Task GivenStaleRevisionAndConcurrentDeletes_WhenDeleting_ThenConflictWhileActiveAndOneTrashWinner()
    {
        using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var p=await Add(s,f,scoped);Authorize(RegistrationApiFixture.Token(s.Actor));var before=await Snapshot(s);using var stale=await Send(p.ProfileId.ToString(),s.Access,new(2));await Failure(stale,409,"revision_conflict");Assert.Equal(before,await Snapshot(s));var results=await Task.WhenAll(Send(p.ProfileId.ToString(),s.Access),Send(p.ProfileId.ToString(),s.Access));Assert.Single(results,x=>x.StatusCode==HttpStatusCode.OK);await Failure(Assert.Single(results,x=>x.StatusCode!=HttpStatusCode.OK),404,"not_found");foreach(var r in results)r.Dispose();await using var db=fixture.Context();Assert.Single(await db.TrashOperations.Where(x=>x.AccountId==s.InternalId).ToArrayAsync());
    }
    [FunctionalTheory][InlineData("trash_entry")][InlineData("retention_work_item")]
    public async Task GivenUnavailableTrashDependency_WhenDeleting_Then503AndRollbackAllState(string table)
    {
        using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var p=await Add(s,f,scoped);Authorize(RegistrationApiFixture.Token(s.Actor));var before=await Snapshot(s);await using var db=fixture.Context();await db.Database.ExecuteSqlRawAsync(table=="trash_entry"?"ALTER TABLE cerberus.trash_entry RENAME TO fixture_unavailable_trash":"ALTER TABLE cerberus.retention_work_item RENAME TO fixture_unavailable_trash");try{using var response=await Send(p.ProfileId.ToString(),s.Access);await Failure(response,503,"persistence_unavailable");}finally{await db.Database.ExecuteSqlRawAsync(table=="trash_entry"?"ALTER TABLE cerberus.fixture_unavailable_trash RENAME TO trash_entry":"ALTER TABLE cerberus.fixture_unavailable_trash RENAME TO retention_work_item");}Assert.Equal(before,await Snapshot(s));Assert.False(await db.TrashOperations.AnyAsync(x=>x.AccountId==s.InternalId));using var retry=await Send(p.ProfileId.ToString(),s.Access);await Profile(retry);
    }
    private Task<HttpResponseMessage> Get(string path,string access){var r=new HttpRequestMessage(HttpMethod.Get,path);r.Headers.Add("X-Cerberus-Vault-Access",access);return Gateway.Client.SendAsync(r);}
    private sealed record State(Guid Actor,Guid AccountId,long InternalId,string Access);
    private async Task<State> Setup(ProtectionFixture f)
    {var actor=Guid.NewGuid();fixture.Users[actor+"@example.test"]=new(actor,"fixture-password");await using var db=fixture.Context();var a=new Account{PublicId=Guid.NewGuid(),HeimdallPublicId=actor,DetailsEnvelope=JsonSerializer.SerializeToUtf8Bytes(Envelope(),ProtectionFixture.Json)};db.Accounts.Add(a);await db.SaveChangesAsync();var access=ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32));OpaqueAccessHandle.TryHash(access,out var verifier);db.VaultProtections.Add(new(){AccountId=a.Id,Material=JsonSerializer.SerializeToUtf8Bytes(f.Material,ProtectionFixture.Json)});db.VaultAccessSessions.Add(new(){AccountId=a.Id,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();return new(actor,a.PublicId,a.Id,access);}
    private async Task<ProfileCreateInput> Add(State s,ProtectionFixture f,ProtectionFixture scoped,string mode="Master")
    {var id=Guid.NewGuid();var input=new ProfileCreateInput(id,Envelope(),new(mode,scoped.Material.UnlockVerifier,f.Wrap(s.AccountId,"profile",id,s.Actor),mode=="PerProfile"?scoped.Material.PasswordWrapper:null),DateTimeOffset.UtcNow,[],[],[]);using var request=new HttpRequestMessage(HttpMethod.Post,"/api/profiles"){Content=JsonContent.Create(input)};request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));request.Headers.Add("X-Cerberus-Vault-Access",s.Access);using var response=await Gateway.Client.SendAsync(request);Assert.Equal(HttpStatusCode.Created,response.StatusCode);return input;}
    private static EncryptedEnvelope Envelope()=>new("cerberus-content-v1",1,ProtectionFixture.Encode(new byte[32]),ProtectionFixture.Encode(new byte[12]),"AQID",ProtectionFixture.Encode(new byte[16]));
    private Task<HttpResponseMessage> Send(string id,string? access,DeleteBody? input=null)
    {var request=new HttpRequestMessage(HttpMethod.Delete,"/api/profiles/"+id){Content=JsonContent.Create(input??Input())};if(access is not null)request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",access);return Gateway.Client.SendAsync(request);}
    private async Task<string> Snapshot(State s)
    {await using var db=fixture.Context();return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync()});}
    private static async Task<DeleteProfileOutput> Profile(HttpResponseMessage response)
    {Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);var r=await response.Content.ReadFromJsonAsync<DataOutput<DeleteProfileOutput>>();Assert.True(r!.Success);return r.Data!;}
    private static async Task Failure(HttpResponseMessage response,int status,string? error=null)
    {Assert.Equal((HttpStatusCode)status,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);var r=await response.Content.ReadFromJsonAsync<DataOutput<DeleteProfileOutput>>();Assert.False(r!.Success);Assert.Null(r.Data);if(error is not null)Assert.Contains(error,r.Errors);}
    private sealed record DeleteBody(long ExpectedRevision);
    private static DeleteBody Input()=>new(1);
}
