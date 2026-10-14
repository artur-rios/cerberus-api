using System.Text;
using System.Text.Json;
using System.Net;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Configuration.Enums;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public partial class AccountClosureCancellationHttpTests(RegistrationApiFixture fixture):WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenPendingClosureAndRetainedHeimdallIdentity_WhenLoggingInAndCancelling_ThenRestoreActiveWithoutOldAccess(bool mfa)
    {
        var actor=Guid.NewGuid();var email=Guid.NewGuid()+"@example.test";fixture.Users[email]=new(actor,"fixture-password",Mfa:mfa);
        await using var db=fixture.Context();var account=new Account{PublicId=Guid.NewGuid(),HeimdallPublicId=actor,DetailsEnvelope=[1,2,3]};db.Accounts.Add(account);await db.SaveChangesAsync();
        Authorize(RegistrationApiFixture.Token(actor));
        using(var close=await Send("/api/accounts/me/closure","{\"expectedRevision\":1}")){Assert.Equal(HttpStatusCode.OK,close.StatusCode);}
        Gateway.Client.DefaultRequestHeaders.Authorization=null;
        using(var denied=await Send("/api/auth/login",JsonSerializer.Serialize(new{email,password="fixture-password"})))
        {
            if(!mfa)Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
            else
            {
                using var ordinary=JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
                var ordinaryChallenge=ordinary.RootElement.GetProperty("data").GetProperty("identity").GetProperty("challengeToken").GetString();
                using var ordinaryVerify=await Send("/api/auth/2fa/verify",JsonSerializer.Serialize(new{challengeToken=ordinaryChallenge,code="123456"}));
                Assert.Equal(HttpStatusCode.Unauthorized,ordinaryVerify.StatusCode);
            }
        }
        using var login=await Send("/api/auth/closure/login",JsonSerializer.Serialize(new{email,password="fixture-password"}));Assert.Equal(HttpStatusCode.OK,login.StatusCode);Assert.True(login.Headers.CacheControl?.NoStore);
        var raw=await login.Content.ReadAsStringAsync();using var json=JsonDocument.Parse(raw);var data=json.RootElement.GetProperty("data");
        await Capture(mfa?"challenge":"login",raw);
        await Capture("login-request",JsonSerializer.Serialize(new{email,password="fixture-password"}));
        string token;
        if(mfa)
        {
            Assert.True(data.GetProperty("identity").GetProperty("requiresTwoFactor").GetBoolean());
            var challenge=data.GetProperty("identity").GetProperty("challengeToken").GetString();
            using var verify=await Send("/api/auth/closure/2fa/verify",JsonSerializer.Serialize(new{challengeToken=challenge,code="123456"}));
            await Capture("verify-request",JsonSerializer.Serialize(new{challengeToken=challenge,code="123456"}));
            Assert.Equal(HttpStatusCode.OK,verify.StatusCode);Assert.True(verify.Headers.CacheControl?.NoStore);raw=await verify.Content.ReadAsStringAsync();
            await Capture("verified",raw);
            using var verified=JsonDocument.Parse(raw);token=verified.RootElement.GetProperty("data").GetProperty("identity").GetProperty("token").GetString()!;
            Assert.Equal(account.PublicId,verified.RootElement.GetProperty("data").GetProperty("account").GetProperty("accountId").GetGuid());
        }
        else {token=data.GetProperty("identity").GetProperty("token").GetString()!;Assert.Equal(account.PublicId,data.GetProperty("account").GetProperty("accountId").GetGuid());}
        Assert.DoesNotContain("vaultAccess",raw);Assert.DoesNotContain("envelope",raw);Authorize(token);
        using var cancel=await Send("/api/accounts/me/closure/cancel","{\"expectedRevision\":2}");Assert.Equal(HttpStatusCode.OK,cancel.StatusCode);Assert.True(cancel.Headers.CacheControl?.NoStore);
        var cancelledRaw=await cancel.Content.ReadAsStringAsync();
        await Capture(mfa?"cancel-mfa":"cancel",cancelledRaw);await Capture("cancel-request","{\"expectedRevision\":2}");
        using var cancelled=JsonDocument.Parse(cancelledRaw);Assert.Equal(3,cancelled.RootElement.GetProperty("data").GetProperty("revision").GetInt64());
        var saved=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==account.Id);Assert.Equal(AccountState.Active,saved.State);Assert.Null(saved.ClosurePurgeAt);Assert.Equal(new byte[]{1,2,3},saved.DetailsEnvelope);
        Assert.NotNull(await db.RetentionWorkItems.Where(x=>x.OperationKey=="account-purge/"+account.PublicId).Select(x=>x.CompletedAt).SingleAsync());
    }
    private static async Task Capture(string name,string body)
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"account-closure-cancellation-contract-fixtures");Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory,name+".json"),body);
    }
    private Task<HttpResponseMessage> Send(string route,string body)=>Gateway.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post,route){Content=new StringContent(body,Encoding.UTF8,"application/json")});
}
