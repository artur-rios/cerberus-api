using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Configuration.Enums;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public partial class AccountDeletionHttpTests(RegistrationApiFixture fixture):WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenFreshOwnerAndAccountAccess_WhenDeleting_ThenReturnMinimalErasureAndRetainHeimdallIdentity()
    {
        var s=await Setup();using var request=Request(s);using var response=await Gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);var raw=await response.Content.ReadAsStringAsync();
        using var json=JsonDocument.Parse(raw);var data=json.RootElement.GetProperty("data");Assert.Equal(s.AccountId,data.GetProperty("accountId").GetGuid());Assert.Equal("completed",data.GetProperty("state").GetString());
        Assert.DoesNotContain("envelope",raw);Assert.DoesNotContain(s.Handle,raw);Assert.DoesNotContain("token",raw);
        var folder=Path.Combine(AppContext.BaseDirectory,"account-deletion-contract-fixtures");Directory.CreateDirectory(folder);await File.WriteAllTextAsync(Path.Combine(folder,"completed.json"),raw);await File.WriteAllTextAsync(Path.Combine(folder,"request.json"),"{\"expectedRevision\":1}");
        await using var db=fixture.Context();Assert.False(await db.Accounts.AnyAsync(x=>x.PublicId==s.AccountId));
        using var login=await Gateway.Client.PostAsync("/api/auth/login",new StringContent(JsonSerializer.Serialize(new{email=s.Email,password="fixture-password"}),Encoding.UTF8,"application/json"));Assert.Equal(HttpStatusCode.OK,login.StatusCode);
        using var identity=JsonDocument.Parse(await login.Content.ReadAsStringAsync());Assert.Equal(JsonValueKind.Null,identity.RootElement.GetProperty("data").GetProperty("account").ValueKind);
    }
    private sealed record State(Guid Actor,Guid AccountId,string Handle,string Email);
    private async Task<State> Setup(string kind="")
    {
        Gateway.Client.DefaultRequestHeaders.Authorization=null;
        var actor=Guid.NewGuid();var email=Guid.NewGuid()+"@example.test";fixture.Users[email]=new(actor,"fixture-password",Deleted:kind=="deletedIdentity",IdentityUnavailable:kind=="identityUnavailable");
        var handle=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');Assert.True(OpaqueAccessHandle.TryHash(handle,out var verifier));
        var account=new Account{PublicId=Guid.NewGuid(),HeimdallPublicId=actor,DetailsEnvelope=[1,2,3]};await using var db=fixture.Context();
        if(kind!="missing")
        {
            db.Accounts.Add(account);await db.SaveChangesAsync();db.VaultAccessSessions.Add(new(){AccountId=account.Id,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=kind=="expired"?DateTimeOffset.UtcNow.AddMinutes(-1):DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1,Revoked=kind=="revoked",ProfileId=kind=="selected"?42:null});await db.SaveChangesAsync();
        }
        return new(actor,account.PublicId,handle,email);
    }
    private static HttpRequestMessage Request(State s,string body="{\"expectedRevision\":1}")
    {
        var request=new HttpRequestMessage(HttpMethod.Delete,"/api/accounts/me/permanent"){Content=new StringContent(body,Encoding.UTF8,"application/json")};
        request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));request.Headers.Add("X-Cerberus-Vault-Access",s.Handle);return request;
    }
}
