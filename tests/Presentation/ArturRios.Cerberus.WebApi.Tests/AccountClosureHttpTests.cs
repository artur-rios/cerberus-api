using System.Net;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Configuration.Enums;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class AccountClosureHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenFreshOwner_WhenClosingAndRetrying_ThenReturnSameDeadlineWithoutPayloadOrNewAccess()
    {
        var actor = Guid.NewGuid(); fixture.Users[Guid.NewGuid() + "@example.test"] = new(actor, "fixture-password");
        await using var db = fixture.Context(); var account = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = actor, DetailsEnvelope = [1, 2, 3] };
        db.Accounts.Add(account); await db.SaveChangesAsync(); Authorize(RegistrationApiFixture.Token(actor)); string? deadline = null;
        for (var n = 0; n < 2; n++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/accounts/me/closure") { Content = new StringContent("{\"expectedRevision\":1}", Encoding.UTF8, "application/json") };
            using var response = await Gateway.Client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
            var raw = await response.Content.ReadAsStringAsync();
            var directory=Path.Combine(AppContext.BaseDirectory,"account-closure-contract-fixtures");Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory,n==0?"pending.json":"replay.json"),raw);
            await File.WriteAllTextAsync(Path.Combine(directory,"request.json"),"{\"expectedRevision\":1}");
            Assert.DoesNotContain("envelope", raw); Assert.DoesNotContain("ciphertext", raw); Assert.DoesNotContain("token", raw);
            using var json = JsonDocument.Parse(raw); var data = json.RootElement.GetProperty("data"); Assert.Equal(account.PublicId, data.GetProperty("accountId").GetGuid());
            Assert.Equal(2, data.GetProperty("revision").GetInt64()); Assert.Equal(2, data.GetProperty("revocationGeneration").GetInt64()); Assert.Equal(n == 1, data.GetProperty("alreadyPending").GetBoolean());
            var value = data.GetProperty("closureDeadline").GetString(); if (deadline is null) deadline = value; else Assert.Equal(deadline, value);
            Assert.Equal(TimeSpan.FromDays(30), data.GetProperty("closureDeadline").GetDateTimeOffset() - data.GetProperty("closureRequestedAt").GetDateTimeOffset());
        }
        Assert.Equal(AccountState.ClosurePending, await db.Accounts.Where(x => x.Id == account.Id).Select(x => x.State).SingleAsync());
        Assert.Equal(new byte[] { 1, 2, 3 }, await db.Accounts.Where(x => x.Id == account.Id).Select(x => x.DetailsEnvelope).SingleAsync());
    }
    [FunctionalTheory]
    [InlineData("{}")][InlineData("null")][InlineData("[]")][InlineData("{")]
    [InlineData("{\"expectedRevision\":1,\"expectedRevision\":1}")]
    [InlineData("{\"expectedRevision\":1,\"actor\":\"forged\"}")]
    [InlineData("{\"ExpectedRevision\":1}")][InlineData("{\"expectedRevision\":\"1\"}")]
    [InlineData("{\"expectedRevision\":1.0}")][InlineData("{\"expectedRevision\":1e0}")]
    [InlineData("{\"expectedRevision\":null}")][InlineData("{\"expectedRevision\":0}")]
    [InlineData("{\"expectedRevision\":-1}")][InlineData("{\"expectedRevision\":9007199254740992}")]
    public async Task GivenInvalidClosureBody_WhenPosting_ThenRejectWithoutChangingState(string body)
    {
        var (actor, account) = await Owner();
        using var request = ClosureRequest(actor, body); using var response = await Gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Contains("validation_failed", await response.Content.ReadAsStringAsync());
        await Unchanged(account);
    }
    [FunctionalTheory]
    [InlineData("query",400)][InlineData("duplicateHeader",400)][InlineData("empty",400)]
    [InlineData("oversized",413)][InlineData("noMedia",400)][InlineData("text",400)]
    [InlineData("parameter",400)][InlineData("charset",400)][InlineData("duplicateCharset",400)]
    [InlineData("compression",400)][InlineData("bom",400)][InlineData("utf8",400)]
    [InlineData("noToken",401)][InlineData("badToken",401)][InlineData("scope",401)]
    [InlineData("deletedIdentity",401)][InlineData("identityUnavailable",503)]
    [InlineData("missingAccount",404)][InlineData("revision",409)]
    public async Task GivenInvalidTransportOrCurrentAuthority_WhenClosing_ThenFailWithoutMutation(string kind, int status)
    {
        var (actor, account) = await Owner(kind);
        using var request = ClosureRequest(actor, kind == "revision" ? "{\"expectedRevision\":2}" : "{\"expectedRevision\":1}");
        if (kind == "query") request.RequestUri = new("/api/accounts/me/closure?expectedRevision=1",UriKind.Relative);
        if (kind == "duplicateHeader") request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",new[]{"one","two"});
        if (kind == "noToken") request.Headers.Authorization = null;
        if (kind == "badToken") request.Headers.Authorization = new("Bearer","invalid");
        if (kind == "scope") request.Headers.Authorization = new("Bearer",RegistrationApiFixture.Token(actor,Guid.NewGuid()));
        if (kind is "empty" or "oversized" or "bom" or "utf8")
        {
            request.Content = new ByteArrayContent(kind switch { "empty" => [], "oversized" => Encoding.UTF8.GetBytes(new string(' ',65537)),
                "bom" => [239,187,191,..Encoding.UTF8.GetBytes("{\"expectedRevision\":1}")], _ => [0xc3,0x28] });
            request.Content.Headers.ContentType = new("application/json");
        }
        if (kind == "compression") request.Content!.Headers.ContentEncoding.Add("gzip");
        if (kind is "noMedia" or "text" or "parameter" or "charset" or "duplicateCharset")
        {
            request.Content!.Headers.Remove("Content-Type");
            if (kind != "noMedia") request.Content.Headers.TryAddWithoutValidation("Content-Type",kind switch {
                "text" => "text/plain", "parameter" => "application/json; boundary=forged", "charset" => "application/json; charset=utf-16",
                _ => "application/json; charset=utf-8; charset=utf-8" });
        }
        using var response = await Gateway.Client.SendAsync(request); Assert.Equal(status,(int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore); await Unchanged(account);
    }

    [FunctionalTheory][InlineData("missing")][InlineData("old")][InlineData("future")][InlineData("duplicate")][InlineData("malformed")]
    public async Task GivenNonFreshSignedIdentity_WhenClosing_ThenRejectBeforeMutation(string kind)
    {
        var (actor,account)=await Owner(); var claims=new List<System.Security.Claims.Claim> {
            new("id",actor.ToString()),new("roleId","3"),new("scopeId",RegistrationApiFixture.Scope.ToString()) };
        if(kind!="missing") claims.Add(new("iat",kind=="malformed" ? "malformed" :
            (DateTimeOffset.UtcNow.ToUnixTimeSeconds()+(kind=="old" ? -120 : kind=="future" ? 120 : 0)).ToString(),
            kind=="malformed" ? System.Security.Claims.ClaimValueTypes.String : System.Security.Claims.ClaimValueTypes.Integer64));
        if(kind=="duplicate") claims.Add(new("iat",DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),System.Security.Claims.ClaimValueTypes.Integer64));
        var token=new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            "fixture-issuer","fixture-audience",claims,DateTime.UtcNow.AddMinutes(-5),DateTime.UtcNow.AddMinutes(5),
            new Microsoft.IdentityModel.Tokens.SigningCredentials(new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                Encoding.ASCII.GetBytes("fixture-only-signing-key-32-characters")),Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256)));
        using var request=ClosureRequest(actor,"{\"expectedRevision\":1}"); request.Headers.Authorization=new("Bearer",token);
        using var response=await Gateway.Client.SendAsync(request);Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
        await Unchanged(account);
    }
    private async Task<(Guid Actor, Account Account)> Owner(string kind = "")
    {
        var actor = Guid.NewGuid(); fixture.Users[Guid.NewGuid()+"@example.test"] = new(actor,"fixture-password",
            Deleted:kind=="deletedIdentity",IdentityUnavailable:kind=="identityUnavailable");
        var account = new Account { PublicId=Guid.NewGuid(),HeimdallPublicId=actor,DetailsEnvelope=[1,2,3] };
        await using var db = fixture.Context(); if(kind!="missingAccount") {db.Accounts.Add(account);await db.SaveChangesAsync();}
        return(actor,account);
    }
    private static HttpRequestMessage ClosureRequest(Guid actor,string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,"/api/accounts/me/closure") {
            Content=new StringContent(body,Encoding.UTF8,"application/json") };
        request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(actor)); return request;
    }
    private async Task Unchanged(Account a)
    {
        await using var db = fixture.Context(); if(a.Id==0) {Assert.False(await db.Accounts.AnyAsync(x=>x.PublicId==a.PublicId));return;}
        var saved=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==a.Id);Assert.Equal(AccountState.Active,saved.State);
        Assert.Equal(1,saved.Revision);Assert.Equal(1,saved.RevocationGeneration);Assert.Null(saved.ClosureRequestedAt);
        Assert.False(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="account-purge/"+a.PublicId));
    }

}
