using Microsoft.AspNetCore.Mvc.Testing;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class ProfileListHttpTests(RegistrationApiFixture fixture):WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenOtherOwnersTrashAndErasure_WhenPaging_ThenOnlyOwnedVisibleOpaqueItemsAndNoTotals()
    {
        using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var other=await Setup(f);
        var hidden=await Add(s,f,scoped);var erased=await Add(s,f,scoped);await Add(other,f,scoped);var one=await Add(s,f,scoped);var two=await Add(s,f,scoped);
        await using(var db=fixture.Context()){
            await db.Profiles.Where(x=>x.PublicId==hidden).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,DateTimeOffset.UtcNow));
            db.TerminalErasures.Add(new(){ResourceId=erased,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        Authorize(RegistrationApiFixture.Token(s.Actor));using var response=await Send("?pageSize=1",s.Access);var first=await Page(response);
        Assert.Equal(one,Assert.Single(first.Items).ProfileId);Assert.NotNull(first.NextCursor);var later=await Add(s,f,scoped);
        using var nextResponse=await Send("?pageSize=1&cursor="+first.NextCursor,s.Access);var second=await Page(nextResponse);Assert.Equal(two,Assert.Single(second.Items).ProfileId);Assert.Null(second.NextCursor);Assert.DoesNotContain(second.Items,x=>x.ProfileId==later);
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal(new[]{"items","nextCursor"},doc.RootElement.GetProperty("data").EnumerateObject().Select(x=>x.Name).Order().ToArray());
        Assert.Equal(new[]{"collectionIds","editedAt","envelope","folderIds","keyWrappers","profileId","recordIds","revision","serverSequence"},doc.RootElement.GetProperty("data").GetProperty("items")[0].EnumerateObject().Select(x=>x.Name).Order().ToArray());
        await using var check=fixture.Context();Assert.Equal(5,await check.Profiles.CountAsync(x=>x.AccountId==s.InternalId));
    }
    [FunctionalFact]
    public async Task GivenOwnerWithNoProfiles_WhenListing_ThenEmptySuccess()
    {using var f=new ProtectionFixture();var s=await Setup(f);Authorize(RegistrationApiFixture.Token(s.Actor));using var response=await Send("",s.Access);var p=await Page(response);Assert.Empty(p.Items);Assert.Null(p.NextCursor);}
    [FunctionalFact]
    public async Task GivenSelectedAccess_WhenListing_ThenOnlyThatProfile()
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);var id=await Add(s,f,scoped);await Add(s,f,scoped);
        await using(var db=fixture.Context()){var p=await db.Profiles.SingleAsync(x=>x.PublicId==id);await db.VaultAccessSessions.Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ProfileId,p.Id));}
        Authorize(RegistrationApiFixture.Token(s.Actor));using var response=await Send("",s.Access);Assert.Equal(id,Assert.Single((await Page(response)).Items).ProfileId);}
    [FunctionalTheory]
    [InlineData("?pageSize=0")][InlineData("?pageSize=-1")][InlineData("?pageSize=101")][InlineData("?pageSize=2147483648")]
    [InlineData("?pageSize=1.0")][InlineData("?pageSize=+1")][InlineData("?pageSize=%201")][InlineData("?pageSize=01")][InlineData("?pageSize=")]
    [InlineData("?pageSize=1&pageSize=2")][InlineData("?PageSize=1")][InlineData("?actor=00000000-0000-0000-0000-000000000001")]
    [InlineData("?cursor=")][InlineData("?cursor=bad")][InlineData("?cursor=a&cursor=b")]
    public async Task GivenInvalidVisibleQuery_WhenListing_Then400WithoutChanges(string query)
    {using var f=new ProtectionFixture();var s=await Setup(f);Authorize(RegistrationApiFixture.Token(s.Actor));using var response=await Send(query,s.Access);await Failure(response,400);await AssertNoMutation(s);}
    [FunctionalTheory][InlineData("missing",401)][InlineData("malformed",400)][InlineData("duplicate",400)][InlineData("body",400)]
    public async Task GivenBadAccessOrGetBody_WhenListing_ThenReject(string kind,int status)
    {using var f=new ProtectionFixture();var s=await Setup(f);Authorize(RegistrationApiFixture.Token(s.Actor));using var request=new HttpRequestMessage(HttpMethod.Get,"/api/profiles");if(kind!="missing")request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",kind=="malformed"?"bad":s.Access);if(kind=="duplicate")request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",s.Access);if(kind=="body")request.Content=new StringContent("{}");using var response=await Gateway.Client.SendAsync(request);await Failure(response,status);await AssertNoMutation(s);}
    [FunctionalTheory][InlineData("empty")][InlineData("http2")]
    public async Task GivenRawEmptyHeaderOrUnknownLengthBody_WhenListing_Then400(string kind)
    {using var f=new ProtectionFixture();var s=await Setup(f);using var host=new WebApplicationFactory<Program>();var context=await host.Server.SendAsync(c=>{c.Request.Method="GET";c.Request.Path="/api/profiles";c.Request.Headers.Authorization="Bearer "+RegistrationApiFixture.Token(s.Actor);c.Request.Headers["X-Cerberus-Vault-Access"]=kind=="empty"?"":s.Access;if(kind=="http2")c.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature());});Assert.Equal(400,context.Response.StatusCode);}
    [FunctionalTheory][InlineData("missing",401)][InlineData("wrongscope",401)][InlineData("deleted",401)][InlineData("unavailable",503)]
    public async Task GivenInvalidOrUnavailableIdentity_WhenListing_ThenFailClosed(string kind,int status)
    {using var f=new ProtectionFixture();var s=await Setup(f);if(kind!="missing")Authorize(RegistrationApiFixture.Token(s.Actor,kind=="wrongscope"?Guid.NewGuid():null));
        if(kind is "deleted" or "unavailable"){var pair=fixture.Users.Single(x=>x.Value.Id==s.Actor);fixture.Users[pair.Key]=pair.Value with {Deleted=kind=="deleted",IdentityUnavailable=kind=="unavailable"};}
        using var response=await Send("",s.Access);await Failure(response,status);await AssertNoMutation(s);}
    [FunctionalTheory][InlineData("missing",404)][InlineData("closing",404)][InlineData("erased",404)][InlineData("revoked",403)]
    [InlineData("expired",403)][InlineData("policy",403)][InlineData("generation",403)][InlineData("foreign",403)][InlineData("selection",403)]
    public async Task GivenInvalidCurrentAccountOrAccess_WhenListing_ThenReject(string kind,int status)
    {using var f=new ProtectionFixture();var s=await Setup(f);var actor=s.Actor;
        await using(var db=fixture.Context()){
            if(kind=="missing"){actor=Guid.NewGuid();fixture.Users[actor+"@example.test"]=new(actor,"fixture-password");}
            if(kind=="closing")await db.Accounts.Where(x=>x.Id==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.ClosurePending));
            if(kind=="erased"){db.TerminalErasures.Add(new(){ResourceId=s.AccountId,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
            var v=await db.VaultAccessSessions.SingleAsync(x=>x.AccountId==s.InternalId);if(kind=="revoked")v.Revoked=true;if(kind=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(kind=="policy")v.PolicyRevision++;if(kind=="generation")v.RevocationGeneration++;if(kind=="selection")v.ProfileId=long.MaxValue;await db.SaveChangesAsync();}
        if(kind=="foreign"){var other=await Setup(f);s=s with {Access=other.Access};}
        Authorize(RegistrationApiFixture.Token(actor));using var response=await Send("",s.Access);await Failure(response,status);await AssertNoMutation(s);}
    [FunctionalTheory][InlineData("revoked",403)][InlineData("trashed",200)][InlineData("foreignCursor",400)]
    public async Task GivenPermissionChangesOrCursorSubstitution_WhenContinuing_ThenApplyCurrentVisibility(string kind,int status)
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);await Add(s,f,scoped);var second=await Add(s,f,scoped);Authorize(RegistrationApiFixture.Token(s.Actor));using var first=await Send("?pageSize=1",s.Access);var cursor=(await Page(first)).NextCursor!;
        await using(var db=fixture.Context()){if(kind=="revoked")await db.VaultAccessSessions.Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Revoked,true));if(kind=="trashed")await db.Profiles.Where(x=>x.PublicId==second).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,DateTimeOffset.UtcNow));}
        if(kind=="foreignCursor"){s=await Setup(f);Authorize(RegistrationApiFixture.Token(s.Actor));}
        using var response=await Send("?pageSize=1&cursor="+cursor,s.Access);if(status==200)Assert.Empty((await Page(response)).Items);else await Failure(response,status);}
    [FunctionalTheory][InlineData("unavailable")][InlineData("envelope")][InlineData("recipient")][InlineData("zero")][InlineData("duplicate")]
    public async Task GivenUnavailableOrCorruptProfileStorage_WhenListing_Then503WithoutPartialPayload(string corrupt)
    {using var f=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(f);await Add(s,f,scoped);var second=await Add(s,f,scoped);if(corrupt=="duplicate")await Add(s,f,scoped);Authorize(RegistrationApiFixture.Token(s.Actor));await using var db=fixture.Context();
        if(corrupt=="envelope")await db.Profiles.Where(x=>x.PublicId==second).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Envelope,new byte[]{1}));else if(corrupt=="recipient"){var row=await db.Profiles.SingleAsync(x=>x.PublicId==second);var wrappers=JsonSerializer.Deserialize<ProfileKeyWrappers>(row.KeyWrappers,ProtectionFixture.Json)!;row.KeyWrappers=JsonSerializer.SerializeToUtf8Bytes(wrappers with {MasterKeyWrapper=wrappers.MasterKeyWrapper with {RecipientIdentityId=Guid.NewGuid()}},ProtectionFixture.Json);await db.SaveChangesAsync();}
        else if(corrupt is "zero" or "duplicate"){var first=await db.Profiles.Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ServerSequence).FirstAsync();await db.Profiles.Where(x=>x.PublicId==second).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.ServerSequence,corrupt=="zero"?0:first.ServerSequence));}
        else await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.profile RENAME TO fixture_unavailable_profile");
        try{using var response=await Send(corrupt=="duplicate"?"?pageSize=1":"",s.Access);await Failure(response,503);}finally{if(corrupt=="unavailable")await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_profile RENAME TO profile");}
        Assert.Equal(corrupt=="duplicate"?3:2,await db.Profiles.CountAsync(x=>x.AccountId==s.InternalId));}
    private sealed record State(Guid Actor,Guid AccountId,long InternalId,string Access);
    private async Task<State> Setup(ProtectionFixture f)
    {var actor=Guid.NewGuid();fixture.Users[actor+"@example.test"]=new(actor,"fixture-password");await using var db=fixture.Context();var a=new Account{PublicId=Guid.NewGuid(),HeimdallPublicId=actor,DetailsEnvelope=JsonSerializer.SerializeToUtf8Bytes(Envelope(),ProtectionFixture.Json)};db.Accounts.Add(a);await db.SaveChangesAsync();var access=ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32));OpaqueAccessHandle.TryHash(access,out var verifier);db.VaultProtections.Add(new(){AccountId=a.Id,Material=JsonSerializer.SerializeToUtf8Bytes(f.Material,ProtectionFixture.Json)});db.VaultAccessSessions.Add(new(){AccountId=a.Id,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();return new(actor,a.PublicId,a.Id,access);}
    private async Task<Guid> Add(State s,ProtectionFixture f,ProtectionFixture scoped)
    {var id=Guid.NewGuid();var input=new ProfileCreateInput(id,Envelope(),new("Master",scoped.Material.UnlockVerifier,f.Wrap(s.AccountId,"profile",id,s.Actor),null),DateTimeOffset.UtcNow,[],[],[]);using var request=new HttpRequestMessage(HttpMethod.Post,"/api/profiles"){Content=JsonContent.Create(input)};request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));request.Headers.Add("X-Cerberus-Vault-Access",s.Access);using var response=await Gateway.Client.SendAsync(request);Assert.Equal(HttpStatusCode.Created,response.StatusCode);return id;}
    private static EncryptedEnvelope Envelope()=>new("cerberus-content-v1",1,ProtectionFixture.Encode(new byte[32]),ProtectionFixture.Encode(new byte[12]),"AQID",ProtectionFixture.Encode(new byte[16]));
    private Task<HttpResponseMessage> Send(string query,string? access)
    {var request=new HttpRequestMessage(HttpMethod.Get,"/api/profiles"+query);if(access is not null)request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",access);return Gateway.Client.SendAsync(request);}
    private async Task AssertNoMutation(State s)
    {await using var db=fixture.Context();Assert.False(await db.Profiles.AnyAsync(x=>x.AccountId==s.InternalId));Assert.Equal(1,(await db.Accounts.SingleAsync(x=>x.Id==s.InternalId)).Revision);}
    private static async Task<ProfileListOutput> Page(HttpResponseMessage response)
    {Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);var r=await response.Content.ReadFromJsonAsync<DataOutput<ProfileListOutput>>();Assert.True(r!.Success);return r.Data!;}
    private static async Task Failure(HttpResponseMessage response,int status)
    {Assert.Equal((HttpStatusCode)status,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.False(doc.RootElement.GetProperty("success").GetBoolean());if(doc.RootElement.TryGetProperty("data",out var data))Assert.Equal(JsonValueKind.Null,data.ValueKind);}
    private sealed class BodyFeature:IHttpRequestBodyDetectionFeature {public bool CanHaveBody=>true;}
}
