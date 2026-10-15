using System.Net;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

public partial class AccountClosureCancellationHttpTests
{
    [FunctionalTheory]
    [InlineData("{}")][InlineData("null")][InlineData("[]")][InlineData("{")]
    [InlineData("{\"expectedRevision\":2,\"expectedRevision\":2}")]
    [InlineData("{\"expectedRevision\":2,\"actor\":\"forged\"}")][InlineData("{\"ExpectedRevision\":2}")]
    [InlineData("{\"expectedRevision\":\"2\"}")][InlineData("{\"expectedRevision\":2.0}")][InlineData("{\"expectedRevision\":2e0}")]
    [InlineData("{\"expectedRevision\":null}")][InlineData("{\"expectedRevision\":0}")]
    [InlineData("{\"expectedRevision\":-1}")][InlineData("{\"expectedRevision\":9007199254740992}")]
    public async Task GivenInvalidCancellationBody_WhenPosting_ThenRejectAndKeepPending(string body)
    {
        var s=await Pending();var before=await Snapshot(s.Account);using var request=CancelRequest(s.Actor,body);
        using var response=await Gateway.Client.SendAsync(request);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        Assert.Contains("validation_failed",await response.Content.ReadAsStringAsync());Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(before,await Snapshot(s.Account));
    }
    [FunctionalTheory][InlineData("query",400)][InlineData("duplicateHeader",400)][InlineData("empty",400)]
    [InlineData("oversized",413)][InlineData("noMedia",400)][InlineData("text",400)][InlineData("parameter",400)]
    [InlineData("charset",400)][InlineData("duplicateCharset",400)][InlineData("compression",400)]
    [InlineData("bom",400)][InlineData("utf8",400)][InlineData("noToken",401)][InlineData("badToken",401)]
    [InlineData("scope",401)][InlineData("deleted",401)][InlineData("unavailable",503)]
    [InlineData("missing",404)][InlineData("active",409)][InlineData("expired",410)][InlineData("revision",409)]
    public async Task GivenRejectedTransportOrCurrentIdentityState_WhenCancelling_ThenNoMutation(string kind,int status)
    {
        var s=await Pending();await using(var db=fixture.Context())
        {
            if(kind=="missing")await db.Accounts.Where(x=>x.Id==s.Account.Id).ExecuteDeleteAsync();
            if(kind=="expired")await ExpireHttp(db,s.Account);
        }
        if(kind=="active")
        {using var first=CancelRequest(s.Actor,"{\"expectedRevision\":2}");using var success=await Gateway.Client.SendAsync(first);Assert.Equal(HttpStatusCode.OK,success.StatusCode);}
        if(kind is "deleted" or "unavailable")fixture.Users[s.Email]=fixture.Users[s.Email] with{Deleted=kind=="deleted",IdentityUnavailable=kind=="unavailable"};
        var before=await Snapshot(s.Account);using var request=CancelRequest(s.Actor,kind=="revision"?"{\"expectedRevision\":1}":"{\"expectedRevision\":2}");
        if(kind=="query")request.RequestUri=new("/api/accounts/me/closure/cancel?expectedRevision=2",UriKind.Relative);
        if(kind=="duplicateHeader")request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",new[]{"one","two"});
        if(kind=="noToken")request.Headers.Authorization=null;if(kind=="badToken")request.Headers.Authorization=new("Bearer","invalid");
        if(kind=="scope")request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor,Guid.NewGuid()));
        if(kind is "empty" or "oversized" or "bom" or "utf8")
        {
            request.Content=new ByteArrayContent(kind switch{"empty"=>[],"oversized"=>Encoding.UTF8.GetBytes(new string(' ',65537)),
                "bom"=>[239,187,191,..Encoding.UTF8.GetBytes("{\"expectedRevision\":2}")],_=>[0xc3,0x28]});request.Content.Headers.ContentType=new("application/json");
        }
        if(kind=="compression")request.Content!.Headers.ContentEncoding.Add("gzip");
        if(kind is "noMedia" or "text" or "parameter" or "charset" or "duplicateCharset")
        {
            request.Content!.Headers.Remove("Content-Type");if(kind!="noMedia")request.Content.Headers.TryAddWithoutValidation("Content-Type",kind switch{
                "text"=>"text/plain","parameter"=>"application/json; boundary=forged","charset"=>"application/json; charset=utf-16",_=>"application/json; charset=utf-8; charset=utf-8"});
        }
        using var response=await Gateway.Client.SendAsync(request);Assert.Equal(status,(int)response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(before,await Snapshot(s.Account));
    }
    [FunctionalTheory][InlineData("active",401)][InlineData("expired",410)][InlineData("deleted",401)]
    [InlineData("unavailable",503)][InlineData("password",401)][InlineData("forbidden",403)]
    public async Task GivenUnavailableOrNonRecoverableIdentity_WhenUsingRecoveryLogin_ThenNoTokenOrVaultAccess(string kind,int status)
    {
        var s=await Pending(kind=="forbidden"?"forbidden-login-":"");
        if(kind=="active"){using var first=CancelRequest(s.Actor,"{\"expectedRevision\":2}");using var success=await Gateway.Client.SendAsync(first);Assert.Equal(HttpStatusCode.OK,success.StatusCode);}
        if(kind=="expired"){await using var db=fixture.Context();await ExpireHttp(db,s.Account);}
        if(kind is "deleted" or "unavailable")fixture.Users[s.Email]=fixture.Users[s.Email] with{Deleted=kind=="deleted",IdentityUnavailable=kind=="unavailable"};
        var before=await Snapshot(s.Account);using var response=await Send("/api/auth/closure/login",JsonSerializer.Serialize(new{email=s.Email,password=kind=="password"?"wrong":"fixture-password"}));
        Assert.Equal(status,(int)response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);var raw=await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("token",raw);Assert.DoesNotContain("envelope",raw);Assert.DoesNotContain("vaultAccess",raw);Assert.Equal(before,await Snapshot(s.Account));
    }
    [FunctionalTheory][InlineData("{}")][InlineData("null")][InlineData("{\"email\":\"owner@example.test\"}")]
    [InlineData("{\"email\":\"owner@example.test\",\"password\":\"password\",\"actor\":\"forged\"}")]
    [InlineData("{\"email\":\"owner@example.test\",\"email\":\"owner@example.test\",\"password\":\"password\"}")]
    [InlineData("{\"email\":\"invalid\",\"password\":\"\"}")]
    public async Task GivenInvalidRecoveryLoginBody_WhenPosting_ThenRejectBeforeReturningIdentity(string body)
    {var s=await Pending();var before=await Snapshot(s.Account);using var response=await Send("/api/auth/closure/login",body);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Equal(before,await Snapshot(s.Account));}
    [FunctionalTheory][InlineData("{}")][InlineData("{\"challengeToken\":\"fake\"}")]
    [InlineData("{\"challengeToken\":\"fake\",\"code\":\"123456\",\"recoveryCode\":\"recovery\"}")]
    [InlineData("{\"challengeToken\":\"fake\",\"code\":\"123456\",\"actor\":\"forged\"}")]
    public async Task GivenInvalidRecoveryMfaBody_WhenPosting_ThenRejectWithoutChangingAccount(string body)
    {var s=await Pending();var before=await Snapshot(s.Account);using var response=await Send("/api/auth/closure/2fa/verify",body);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Equal(before,await Snapshot(s.Account));}
    private async Task<(Guid Actor,string Email,Account Account)> Pending(string prefix="")
    {
        var actor=Guid.NewGuid();var email=prefix+Guid.NewGuid()+"@example.test";fixture.Users[email]=new(actor,"fixture-password");
        await using var db=fixture.Context();var account=new Account{PublicId=Guid.NewGuid(),HeimdallPublicId=actor,DetailsEnvelope=[1,2,3]};db.Accounts.Add(account);await db.SaveChangesAsync();
        Authorize(RegistrationApiFixture.Token(actor));using(var close=await Send("/api/accounts/me/closure","{\"expectedRevision\":1}"))Assert.Equal(HttpStatusCode.OK,close.StatusCode);
        Gateway.Client.DefaultRequestHeaders.Authorization=null;return(actor,email,account);
    }
    private static HttpRequestMessage CancelRequest(Guid actor,string body)
    {var request=new HttpRequestMessage(HttpMethod.Post,"/api/accounts/me/closure/cancel"){Content=new StringContent(body,Encoding.UTF8,"application/json")};request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(actor));return request;}
    private async Task<string> Snapshot(Account account)
    {
        await using var db=fixture.Context();return await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object('account',(SELECT to_jsonb(a) FROM cerberus.account a WHERE id={account.Id}),
              'work',(SELECT to_jsonb(w) FROM cerberus.retention_work_item w WHERE operation_key={"account-purge/"+account.PublicId}))::text AS "Value"
            """).SingleAsync();
    }
    private static async Task ExpireHttp(ArturRios.Cerberus.Data.AppDbContext db,Account a)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.account SET closure_purge_at=statement_timestamp()-interval '1 second',closure_requested_at=statement_timestamp()-interval '720 hours 1 second' WHERE id={a.Id}");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.retention_work_item SET due_at=(SELECT closure_purge_at FROM cerberus.account WHERE id={a.Id}) WHERE operation_key={"account-purge/"+a.PublicId}");
    }
}
