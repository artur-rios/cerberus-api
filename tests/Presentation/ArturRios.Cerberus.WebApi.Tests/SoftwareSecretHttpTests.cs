using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Data;
using ArturRios.Cerberus.Data.Software;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Cerberus.Query.Software;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
namespace ArturRios.Cerberus.WebApi.Tests;
[Collection("Registration host")]
public sealed class SoftwareSecretHttpTests(RegistrationApiFixture fixture)
{
    [FunctionalFact]
    public async Task GivenSoftwareReadWithoutApplicationBearer_WhenRequested_ThenAuthenticateBeforeRetrieval()
    {
        _=fixture;using var host=new WebApplicationFactory<Program>();using var client=host.CreateClient();
        using var response=await client.GetAsync($"/api/software-secrets?grantId={Guid.NewGuid():D}&kind=record&id={Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);Assert.Equal("no-store",response.Headers.CacheControl!.ToString());
    }
    [FunctionalTheory][InlineData("record",false)][InlineData("collection",false)][InlineData("record",true)]
    public async Task GivenLiveApplicationAndNativeProvisioning_WhenGetting_ThenReturnOnlySelectedCiphertextAndRevalidateEveryRead(string kind,bool member)
    {
        var c=await Seed(kind,member);using var identity=new LiveIdentity(c.Identity);using var host=Host(identity);using var client=host.CreateClient();
        var token=Token(c.Identity);
        for(var i=0;i<2;i++)
        {
            using var request=Request(c,token);request.Headers.TryAddWithoutValidation("If-None-Match","*");request.Headers.IfModifiedSince=DateTimeOffset.UtcNow;
            using var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);AssertNoStore(response);
            var raw=await response.Content.ReadAsStringAsync();var output=JsonSerializer.Deserialize<DataOutput<SoftwareSecretOutput>>(raw,ProtectionFixture.Json)!;
            Assert.True(output.Success);Assert.Equal(c.Details.Target,output.Data!.Target);Assert.Equal(c.Details.Provisioning,output.Data.Provisioning);
            Assert.Equal(c.Details.AuthorKey,output.Data.AuthorKey);Assert.Equal(c.Details.RecipientKey,output.Data.RecipientKey);
            using var doc=JsonDocument.Parse(raw);Assert.Equal(new[]{"authorKey","grantId","grantRevision","ownerAccountId","provisioning","recipientKey","target"},doc.RootElement.GetProperty("data").EnumerateObject().Select(x=>x.Name).Order());
            Assert.DoesNotContain("password",raw,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("vaultAccess",raw);Assert.DoesNotContain("email",raw);
            if(i==0)await File.WriteAllTextAsync($"/tmp/cerberus-uc43-http-{kind}-{member}.json",raw);
        }
        Assert.Equal(2,identity.Calls);Assert.Equal(token,identity.LastBearer);
        identity.Mode="revoked";using var denied=Request(c,token);using var revoked=await client.SendAsync(denied);await Failure(revoked,401,"authentication_required");Assert.Equal(3,identity.Calls);
    }
    [FunctionalTheory]
    [InlineData("person")][InlineData("role")][InlineData("expired")][InlineData("audience")][InlineData("scope")][InlineData("kind")][InlineData("signature")][InlineData("credential")][InlineData("duplicateBearer")]
    public async Task GivenInvalidApplicationBearer_WhenGetting_ThenRejectBeforeLiveProvider(string mode)
    {
        var c=await Seed("record",false);using var identity=new LiveIdentity(c.Identity);using var host=Host(identity);using var client=host.CreateClient();
        var token=mode=="person"?RegistrationApiFixture.Token(c.Identity.ApplicationId):Token(c.Identity,mode);
        using var request=Request(c,token);if(mode=="duplicateBearer")request.Headers.TryAddWithoutValidation("Authorization","Bearer "+token);
        using var response=await client.SendAsync(request);await Failure(response,401,"authentication_required");Assert.Equal(0,identity.Calls);
    }
    [FunctionalTheory]
    [InlineData("missing")][InlineData("extra")][InlineData("duplicate")][InlineData("upper")][InlineData("empty")][InlineData("folder")]
    [InlineData("body")][InlineData("vault")][InlineData("proof")][InlineData("wrongCollection")][InlineData("emptyCollection")]
    public async Task GivenInvalidTransport_WhenGetting_ThenRejectBeforeLiveCheck(string mode)
    {
        var c=await Seed("record",false);using var identity=new LiveIdentity(c.Identity);using var host=Host(identity);using var client=host.CreateClient();
        using var request=Request(c,Token(c.Identity));var uri=request.RequestUri!.OriginalString;
        request.RequestUri=new Uri(mode switch{"missing"=>$"/api/software-secrets?kind=record&id={c.Input.Id:D}","extra"=>uri+"&ownerId="+Guid.NewGuid(),"duplicate"=>uri+"&id="+c.Input.Id,"upper"=>uri.Replace(c.Input.Id.ToString(),c.Input.Id.ToString().ToUpperInvariant()),"empty"=>uri.Replace(c.Input.GrantId.ToString(),Guid.Empty.ToString()),"folder"=>uri.Replace("kind=record","kind=folder"),"wrongCollection"=>uri+"&collectionId=not-a-guid","emptyCollection"=>uri+"&collectionId=",_=>uri},UriKind.Relative);
        if(mode=="body")request.Content=new StringContent("{}",Encoding.UTF8,"application/json");
        if(mode is "vault" or "proof")request.Headers.Add(mode=="vault"?"X-Cerberus-Vault-Access":"X-Cerberus-Proof","private-human-authority");
        using var response=await client.SendAsync(request);await Failure(response,400,"validation_failed");Assert.Equal(0,identity.Calls);
    }
    [FunctionalTheory][InlineData("revoked",401)][InlineData("unavailable",503)][InlineData("redirect",503)][InlineData("rate",503)]
    [InlineData("malformed",503)][InlineData("wrongApplication",503)][InlineData("wrongCredential",503)][InlineData("wrongScope",503)][InlineData("expiry",503)]
    public async Task GivenCurrentApplicationDenialOrProviderFailure_WhenGetting_ThenReturnSafeFailureWithoutCiphertext(string mode,int status)
    {
        var c=await Seed("record",false);using var identity=new LiveIdentity(c.Identity){Mode=mode};using var host=Host(identity);using var client=host.CreateClient();
        using var request=Request(c,Token(c.Identity));using var response=await client.SendAsync(request);await Failure(response,status,status==401?"authentication_required":"identity_unavailable");Assert.Equal(1,identity.Calls);
    }
    [FunctionalTheory][InlineData("revoked",404)][InlineData("target",404)][InlineData("trash",404)][InlineData("account",404)][InlineData("binding",503)][InlineData("database",503)]
    public async Task GivenGrantOrStorageChanges_WhenGetting_ThenRecheckCurrentStateAndHideCiphertext(string mode,int status)
    {
        var c=await Seed("record",false);using var identity=new LiveIdentity(c.Identity);using var host=Host(identity,mode=="database");using var client=host.CreateClient();
        await using var db=fixture.Context();
        if(mode=="revoked")await db.SoftwareGrants.Where(x=>x.PublicId==c.Input.GrantId).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.State,SoftwareGrantState.Revoked));
        if(mode=="trash")await db.Records.Where(x=>x.PublicId==c.Input.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.DeletedAt,DateTimeOffset.UtcNow));
        if(mode=="account")await db.Accounts.Where(x=>x.PublicId==c.Details.OwnerAccountId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.ClosurePending));
        if(mode=="binding")await db.SoftwareGrantRecords.Where(x=>x.GrantId==db.SoftwareGrants.Where(g=>g.PublicId==c.Input.GrantId).Select(g=>g.Id).Single()).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,new byte[]{1}));
        if(mode=="target")c=c with{Input=c.Input with{Id=Guid.NewGuid()}};
        using var request=Request(c,Token(c.Identity));using var response=await client.SendAsync(request);await Failure(response,status,status==404?"not_found":"persistence_unavailable");Assert.Equal(1,identity.Calls);
    }
    [FunctionalTheory][InlineData("GET","/api/accounts/me")][InlineData("POST","/api/software-grants")][InlineData("PUT","/api/records/527a1001-8ef5-4c9b-a565-222222222222")][InlineData("DELETE","/api/accounts/me/permanent")]
    public async Task GivenApplicationBearer_WhenCallingHumanRoute_ThenDenyWithoutPersonAuthority(string method,string path)
    {
        var c=await Seed("record",false);using var identity=new LiveIdentity(c.Identity);using var host=Host(identity);using var client=host.CreateClient();
        using var request=new HttpRequestMessage(new HttpMethod(method),path);request.Headers.Authorization=new("Bearer",Token(c.Identity));
        if(method!="GET")request.Content=new StringContent("{}",Encoding.UTF8,"application/json");
        using var response=await client.SendAsync(request);await Failure(response,401,"authentication_required");Assert.Equal(0,identity.Calls);
    }
    private async Task<SoftwareSecretFixture.Case> Seed(string kind,bool member)
    {
        var c=SoftwareSecretFixture.Create(kind,member);c=c with{Identity=c.Identity with{ScopeId=RegistrationApiFixture.Scope}};
        using var protection=new ProtectionFixture();await using var db=fixture.Context();
        var account=new Account{PublicId=c.Details.OwnerAccountId,HeimdallPublicId=Guid.NewGuid(),DetailsEnvelope=Bytes(c.Details.Target.Envelope)};db.Accounts.Add(account);await db.SaveChangesAsync();
        db.VaultProtections.Add(new(){AccountId=account.Id,Material=Bytes(protection.Material with{AuthorKey=c.Details.AuthorKey})});
        VaultRecord? record=null;VaultCollection? collection=null;
        if(kind=="record"){record=new(){AccountId=account.Id,PublicId=c.Input.Id,Envelope=Bytes(c.Details.Target.Envelope),EditedAt=DateTimeOffset.UtcNow};db.Records.Add(record);}
        if(kind=="collection"||member){collection=new(){AccountId=account.Id,PublicId=c.Details.Provisioning.Resource.Id,Envelope=Bytes(c.Details.Provisioning.Resource.Envelope),EditedAt=DateTimeOffset.UtcNow};db.Collections.Add(collection);}
        var grant=new SoftwareGrant{AccountId=account.Id,PublicId=c.Input.GrantId,HeimdallSoftwarePublicId=c.Identity.ApplicationId,RecipientKey=Bytes(c.Details.RecipientKey)};db.SoftwareGrants.Add(grant);await db.SaveChangesAsync();
        if(member||kind=="collection")db.SoftwareGrantCollections.Add(new(){AccountId=account.Id,GrantId=grant.Id,CollectionId=collection!.Id,RecipientKeyEnvelope=Bytes(c.Details.Provisioning.KeyEnvelope)});
        else db.SoftwareGrantRecords.Add(new(){AccountId=account.Id,GrantId=grant.Id,RecordId=record!.Id,RecipientKeyEnvelope=Bytes(c.Details.Provisioning.KeyEnvelope)});
        if(member)db.CollectionRecords.Add(new(){AccountId=account.Id,CollectionId=collection!.Id,RecordId=record!.Id});
        await db.SaveChangesAsync();return c;
    }
    private static WebApplicationFactory<Program> Host(LiveIdentity identity,bool unavailable=false)=>new WebApplicationFactory<Program>().WithWebHostBuilder(builder=>builder.ConfigureServices(services=>
    {
        services.RemoveAll<IHeimdallApplicationClient>();services.AddScoped<IHeimdallApplicationClient>(sp=>new HeimdallApplicationClient(new HttpClient(identity,false){BaseAddress=new("http://fixture.test")},sp.GetRequiredService<CerberusOptions>(),sp.GetRequiredService<HeimdallApplicationTokenValidator>()));
        if(unavailable){services.RemoveAll<ISoftwareSecretStore>();services.AddScoped<ISoftwareSecretStore>(_=>new SoftwareSecretStore(new UnavailableDatabase()));}
    }));
    private sealed class UnavailableDatabase:IDbContextFactory<AppDbContext>
    {public AppDbContext CreateDbContext()=>throw new Npgsql.NpgsqlException("private database failure");}
    private sealed class LiveIdentity(SoftwareIdentity identity):HttpMessageHandler
    {
        public string Mode{get;set;}="active";public int Calls{get;private set;}public string? LastBearer{get;private set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();Assert.Equal("/api/applications/self",request.RequestUri!.AbsolutePath);Calls++;LastBearer=request.Headers.Authorization!.Parameter;
            var status=Mode switch{"revoked"=>401,"unavailable"=>503,"redirect"=>302,"rate"=>429,_=>200};
            var current=Mode switch{"wrongApplication"=>identity with{ApplicationId=Guid.NewGuid()},"wrongCredential"=>identity with{CredentialId=Guid.NewGuid()},"wrongScope"=>identity with{ScopeId=Guid.NewGuid()},"expiry"=>identity with{ExpiresAt=identity.ExpiresAt.AddSeconds(1)},_=>identity};
            var raw=Mode=="malformed"?"private provider failure":JsonSerializer.Serialize(new{success=true,errors=Array.Empty<string>(),messages=Array.Empty<string>(),data=new{current.ApplicationId,name="Fixture Application",current.ScopeId,current.CredentialId,current.ExpiresAt}},ProtectionFixture.Json);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent(raw,Encoding.UTF8,"application/json")});
        }
    }
    private static HttpRequestMessage Request(SoftwareSecretFixture.Case c,string token)
    {
        var request=new HttpRequestMessage(HttpMethod.Get,$"/api/software-secrets?grantId={c.Input.GrantId:D}&kind={c.Input.Kind}&id={c.Input.Id:D}"+(c.Input.CollectionId is {} collection?$"&collectionId={collection:D}":""));request.Headers.Authorization=new("Bearer",token);return request;
    }
    private static string Token(SoftwareIdentity identity,string mode="active")
    {
        var payload=new Dictionary<string,object>{{"iss","fixture-issuer"},{"aud",mode=="audience"?"fixture-audience":"fixture-applications"},{"tokenKind",mode=="kind"?"person":"application"},{"id",identity.ApplicationId.ToString("D")},{"scopeId",(mode=="scope"?Guid.NewGuid():identity.ScopeId).ToString("D")},{"credentialId",mode=="credential"?"invalid":identity.CredentialId.ToString("D")},{"iat",DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds()},{"nbf",DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds()},{"exp",mode=="expired"?DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds():identity.ExpiresAt.ToUnixTimeSeconds()}};
        if(mode=="role")payload["roleId"]=3;
        var signed=ProtectionFixture.Encode(Bytes(new{alg="HS256",typ="JWT"}))+"."+ProtectionFixture.Encode(Bytes(payload));
        return signed+"."+ProtectionFixture.Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(mode=="signature"?"wrong-fixture-only-signing-key-32-characters":"fixture-only-signing-key-32-characters"),Encoding.ASCII.GetBytes(signed)));
    }
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    private static void AssertNoStore(HttpResponseMessage response){Assert.Equal("no-store",response.Headers.CacheControl!.ToString());Assert.Null(response.Headers.ETag);Assert.Null(response.Content.Headers.LastModified);}
    private static async Task Failure(HttpResponseMessage response,int status,string code)
    {
        Assert.Equal((HttpStatusCode)status,response.StatusCode);AssertNoStore(response);var raw=await response.Content.ReadAsStringAsync();using var doc=JsonDocument.Parse(raw);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());Assert.Equal(new[]{code},doc.RootElement.GetProperty("errors").EnumerateArray().Select(x=>x.GetString()));
        if(doc.RootElement.TryGetProperty("data",out var data))Assert.Equal(JsonValueKind.Null,data.ValueKind);Assert.DoesNotContain("private",raw);Assert.DoesNotContain("ciphertext",raw);
    }
}
