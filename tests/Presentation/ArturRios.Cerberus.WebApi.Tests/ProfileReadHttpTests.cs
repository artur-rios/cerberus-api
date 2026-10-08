using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Query.Profiles;
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
public class ProfileReadHttpTests(RegistrationApiFixture fixture):WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenOwnedEncryptedProfile_WhenGetting_ThenExactPublicOpaqueDataAndEmptyLinks(string mode)
    {
        using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var input=await Add(s,f,scoped,mode);Authorize(RegistrationApiFixture.Token(s.Actor));var before=await Snapshot(s);
        using var response=await Send(input.ProfileId.ToString(),s.Access);var p=await Profile(response);Assert.Equal(input.ProfileId,p.ProfileId);Assert.Equal(input.Envelope,p.Envelope);Assert.Equal(input.KeyWrappers,p.KeyWrappers);Assert.Equal(1,p.Revision);Assert.True(p.ServerSequence>0);Assert.Empty(p.RecordIds);Assert.Empty(p.FolderIds);Assert.Empty(p.CollectionIds);Assert.Equal(before,await Snapshot(s));
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal(new[]{"collectionIds","editedAt","envelope","folderIds","keyWrappers","profileId","recordIds","revision","serverSequence"},doc.RootElement.GetProperty("data").EnumerateObject().Select(x=>x.Name).Order().ToArray());
    }
    [FunctionalTheory][InlineData("foreign")][InlineData("trash")][InlineData("erased")][InlineData("missing")]
    public async Task GivenHiddenOrAbsentProfile_WhenGetting_ThenSameNonrevealing404(string kind)
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var owner=kind=="foreign"?await Setup(f):s;var input=await Add(owner,f,scoped);
        await using(var db=fixture.Context()){if(kind=="trash")await db.Profiles.Where(x=>x.PublicId==input.ProfileId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,DateTimeOffset.UtcNow));if(kind=="erased"){db.TerminalErasures.Add(new(){ResourceId=input.ProfileId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}
        Authorize(RegistrationApiFixture.Token(s.Actor));var before=await Snapshot(owner);using var response=await Send((kind=="missing"?Guid.NewGuid():input.ProfileId).ToString(),s.Access);await Failure(response,404,"not_found");Assert.Equal(before,await Snapshot(owner));}
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenValidSelectedSession_WhenGetting_ThenSelectionSucceedsAndOtherOwnProfile404(bool otherTarget)
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var one=await Add(s,f,scoped);var two=await Add(s,f,scoped);await using(var db=fixture.Context()){var p=await db.Profiles.SingleAsync(x=>x.PublicId==one.ProfileId);await db.VaultAccessSessions.Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ProfileId,p.Id));}
        Authorize(RegistrationApiFixture.Token(s.Actor));var before=await Snapshot(s);using var response=await Send((otherTarget?two.ProfileId:one.ProfileId).ToString(),s.Access);if(otherTarget)await Failure(response,404,"not_found");else Assert.Equal(one.ProfileId,(await Profile(response)).ProfileId);Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("bad")][InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("ABCDEFAB-1234-4321-ABCD-123456789ABC")][InlineData("abcdefab12344321abcd123456789abc")]
    [InlineData("{abcdefab-1234-4321-abcd-123456789abc}")][InlineData("%20abcdefab-1234-4321-abcd-123456789abc")]
    public async Task GivenMalformedOrNoncanonicalPath_WhenGetting_Then400(string id)
    {using var f=new ProtectionFixture();var s=await Setup(f);Authorize(RegistrationApiFixture.Token(s.Actor));var before=await Snapshot(s);using var response=await Send(id,s.Access);await Failure(response,400);Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("query",400)][InlineData("body",400)][InlineData("duplicate",400)][InlineData("malformed",400)][InlineData("missing",401)]
    public async Task GivenInvalidQueryHeaderOrBody_WhenGetting_ThenRejectWithoutChanges(string kind,int status)
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var p=await Add(s,f,scoped);Authorize(RegistrationApiFixture.Token(s.Actor));var before=await Snapshot(s);
        using var request=new HttpRequestMessage(HttpMethod.Get,"/api/profiles/"+p.ProfileId+(kind=="query"?"?pageSize=1":""));if(kind!="missing")request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",kind=="malformed"?"bad":s.Access);if(kind=="duplicate")request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",s.Access);if(kind=="body")request.Content=new StringContent("{}");using var response=await Gateway.Client.SendAsync(request);await Failure(response,status);Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("empty")][InlineData("http2")]
    public async Task GivenRawEmptyAccessOrUnknownLengthBody_WhenGetting_Then400(string kind)
    {using var f=new ProtectionFixture();var s=await Setup(f);using var host=new WebApplicationFactory<Program>();var context=await host.Server.SendAsync(c=>{c.Request.Method="GET";c.Request.Path="/api/profiles/"+Guid.NewGuid();c.Request.Headers.Authorization="Bearer "+RegistrationApiFixture.Token(s.Actor);c.Request.Headers["X-Cerberus-Vault-Access"]=kind=="empty"?"":s.Access;if(kind=="http2")c.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature());});Assert.Equal(400,context.Response.StatusCode);Assert.Contains("no-store",context.Response.Headers.CacheControl.ToString());}
    [FunctionalTheory][InlineData("missing",401)][InlineData("wrongscope",401)][InlineData("deleted",401)][InlineData("unavailable",503)]
    public async Task GivenInvalidOrUnavailableIdentity_WhenGetting_ThenFailClosed(string kind,int status)
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var p=await Add(s,f,scoped);if(kind!="missing")Authorize(RegistrationApiFixture.Token(s.Actor,kind=="wrongscope"?Guid.NewGuid():null));if(kind is "deleted" or "unavailable"){var pair=fixture.Users.Single(x=>x.Value.Id==s.Actor);fixture.Users[pair.Key]=pair.Value with {Deleted=kind=="deleted",IdentityUnavailable=kind=="unavailable"};}var before=await Snapshot(s);using var response=await Send(p.ProfileId.ToString(),s.Access);await Failure(response,status);Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("missing",404)][InlineData("closing",404)][InlineData("erased",404)]
    [InlineData("revoked",403)][InlineData("expired",403)][InlineData("policy",403)][InlineData("generation",403)][InlineData("selection",403)]
    public async Task GivenInvalidCurrentAccountOrSession_WhenGetting_ThenDenyUnchanged(string kind,int status)
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var p=await Add(s,f,scoped);var actor=s.Actor;
        await using(var db=fixture.Context()){if(kind=="missing"){actor=Guid.NewGuid();fixture.Users[actor+"@example.test"]=new(actor,"fixture-password");}if(kind=="closing")await db.Accounts.Where(x=>x.Id==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.ClosurePending));if(kind=="erased"){db.TerminalErasures.Add(new(){ResourceId=s.AccountId,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}var v=await db.VaultAccessSessions.SingleAsync(x=>x.AccountId==s.InternalId);if(kind=="revoked")v.Revoked=true;if(kind=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(kind=="policy")v.PolicyRevision++;if(kind=="generation")v.RevocationGeneration++;if(kind=="selection")v.ProfileId=long.MaxValue;await db.SaveChangesAsync();}
        Authorize(RegistrationApiFixture.Token(actor));var before=await Snapshot(s);using var response=await Send(p.ProfileId.ToString(),s.Access);await Failure(response,status);Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("envelope")][InlineData("recipient")][InlineData("sequence")][InlineData("revision")]
    public async Task GivenCorruptStoredVisibleMetadata_WhenGetting_Then503WithoutPartialData(string kind)
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var input=await Add(s,f,scoped);await using(var db=fixture.Context()){var p=await db.Profiles.SingleAsync(x=>x.PublicId==input.ProfileId);if(kind=="envelope")p.Envelope="bad"u8.ToArray();if(kind=="recipient")p.KeyWrappers=JsonSerializer.SerializeToUtf8Bytes(input.KeyWrappers with {MasterKeyWrapper=input.KeyWrappers.MasterKeyWrapper with {RecipientIdentityId=Guid.NewGuid()}},ProtectionFixture.Json);if(kind=="sequence")p.ServerSequence=0;if(kind=="revision")p.Revision=0;await db.SaveChangesAsync();}Authorize(RegistrationApiFixture.Token(s.Actor));var before=await Snapshot(s);using var response=await Send(input.ProfileId.ToString(),s.Access);await Failure(response,503);Assert.Equal(before,await Snapshot(s));}
    [FunctionalFact]
    public async Task GivenUnavailableProfileTable_WhenGetting_Then503Unchanged()
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var p=await Add(s,f,scoped);Authorize(RegistrationApiFixture.Token(s.Actor));var before=await Snapshot(s);await using var db=fixture.Context();await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.profile RENAME TO fixture_unavailable_profile");try{using var response=await Send(p.ProfileId.ToString(),s.Access);await Failure(response,503);}finally{await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_profile RENAME TO profile");}Assert.Equal(before,await Snapshot(s));}
    private sealed record State(Guid Actor,Guid AccountId,long InternalId,string Access);
    private async Task<State> Setup(ProtectionFixture f)
    {var actor=Guid.NewGuid();fixture.Users[actor+"@example.test"]=new(actor,"fixture-password");await using var db=fixture.Context();var a=new Account{PublicId=Guid.NewGuid(),HeimdallPublicId=actor,DetailsEnvelope=JsonSerializer.SerializeToUtf8Bytes(Envelope(),ProtectionFixture.Json)};db.Accounts.Add(a);await db.SaveChangesAsync();var access=ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32));OpaqueAccessHandle.TryHash(access,out var verifier);db.VaultProtections.Add(new(){AccountId=a.Id,Material=JsonSerializer.SerializeToUtf8Bytes(f.Material,ProtectionFixture.Json)});db.VaultAccessSessions.Add(new(){AccountId=a.Id,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();return new(actor,a.PublicId,a.Id,access);}
    private async Task<ProfileCreateInput> Add(State s,ProtectionFixture f,ProtectionFixture scoped,string mode="Master")
    {var id=Guid.NewGuid();var input=new ProfileCreateInput(id,Envelope(),new(mode,scoped.Material.UnlockVerifier,f.Wrap(s.AccountId,"profile",id,s.Actor),mode=="PerProfile"?scoped.Material.PasswordWrapper:null),DateTimeOffset.UtcNow,[],[],[]);using var request=new HttpRequestMessage(HttpMethod.Post,"/api/profiles"){Content=JsonContent.Create(input)};request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));request.Headers.Add("X-Cerberus-Vault-Access",s.Access);using var response=await Gateway.Client.SendAsync(request);Assert.Equal(HttpStatusCode.Created,response.StatusCode);return input;}
    private static EncryptedEnvelope Envelope()=>new("cerberus-content-v1",1,ProtectionFixture.Encode(new byte[32]),ProtectionFixture.Encode(new byte[12]),"AQID",ProtectionFixture.Encode(new byte[16]));
    private Task<HttpResponseMessage> Send(string id,string? access)
    {var request=new HttpRequestMessage(HttpMethod.Get,"/api/profiles/"+id);if(access is not null)request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",access);return Gateway.Client.SendAsync(request);}
    private async Task<string> Snapshot(State s)
    {await using var db=fixture.Context();return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync()});}
    private static async Task<ProfileDetailsOutput> Profile(HttpResponseMessage response)
    {Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);var r=await response.Content.ReadFromJsonAsync<DataOutput<ProfileDetailsOutput>>();Assert.True(r!.Success);return r.Data!;}
    private static async Task Failure(HttpResponseMessage response,int status,string? error=null)
    {Assert.Equal((HttpStatusCode)status,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);var r=await response.Content.ReadFromJsonAsync<DataOutput<ProfileDetailsOutput>>();Assert.False(r!.Success);Assert.Null(r.Data);if(error is not null)Assert.Contains(error,r.Errors);}
    private sealed class BodyFeature:IHttpRequestBodyDetectionFeature{public bool CanHaveBody=>true;}
}
