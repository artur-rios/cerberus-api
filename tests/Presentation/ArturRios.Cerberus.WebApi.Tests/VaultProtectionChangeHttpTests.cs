using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Command.Protection;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Query.Accounts;
using ArturRios.Cerberus.Query.Protection;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Configuration.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class VaultProtectionChangeHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalTheory]
    [InlineData("rewrap")][InlineData("rotate-content")]
    public async Task GivenInitializedUnlockedOwner_WhenChangingAndUnlockingAgain_ThenReturnCoherentCurrentVault(string mode)
    {
        using var client=new ProtectionFixture();var s=await Setup(client,false);Authorize(RegistrationApiFixture.Token(s.Actor));
        using(var init=await Gateway.Client.PostAsJsonAsync("/api/vault/protection",new { accountId=s.Id,expectedAccountRevision=3,material=client.Material }))Assert.Equal(HttpStatusCode.Created,init.StatusCode);
        var access=await Unlock(client,1);var change=Change(s,client,mode);var raw=Bytes(change);var challenge=await Challenge(raw);
        using var response=await Send(raw,challenge,client.Sign(challenge),access);Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);
        var confirmation=(await response.Content.ReadFromJsonAsync<DataOutput<ChangeVaultProtectionOutput>>())!.Data!;
        Assert.Equal(s.Id,confirmation.AccountId);Assert.Equal(2,confirmation.ProtectionRevision);Assert.Equal(2,confirmation.KeyEpoch);Assert.Equal(1,confirmation.RecoveryGeneration);Assert.Equal(mode=="rewrap"?3:4,confirmation.AccountRevision);
        var text=await response.Content.ReadAsStringAsync();Assert.DoesNotContain("material",text);Assert.DoesNotContain("vaultAccess",text);
        using(var material=await Gateway.Client.GetAsync("/api/vault/protection"))
        {Assert.Equal(HttpStatusCode.OK,material.StatusCode);Assert.Equal(change.Material,(await material.Content.ReadFromJsonAsync<DataOutput<VaultProtectionOutput>>())!.Data!.Material);}
        using(var old=await Read(access))await Failure(old,HttpStatusCode.Forbidden);
        var next=await Unlock(client,2);using(var current=await Read(next))
        {
            Assert.Equal(HttpStatusCode.OK,current.StatusCode);var account=(await current.Content.ReadFromJsonAsync<DataOutput<AccountOutput>>())!.Data!;
            Assert.Equal(mode=="rewrap"?3:4,account.Revision);Assert.Equal(mode=="rewrap"?1:2,account.Details.KeyEpoch);
        }
        using var retry=await Send(raw,challenge,client.Sign(challenge),access);await Failure(retry,HttpStatusCode.Conflict);
        await using var db=fixture.Context();var a=await db.Accounts.SingleAsync(x=>x.PublicId==s.Id);Assert.Equal(2,a.RevocationGeneration);
        Assert.Equal(mode=="rewrap"?s.Envelope:Bytes(change.ContentReplacements[0].Envelope),a.DetailsEnvelope);
    }

    [FunctionalTheory]
    [InlineData("anonymous",401)][InlineData("scope",401)][InlineData("deletedIdentity",401)][InlineData("identityUnavailable",503)]
    [InlineData("missing",404)][InlineData("foreign",404)][InlineData("closing",404)][InlineData("erased",404)][InlineData("noProtection",404)]
    [InlineData("staleProtection",409)][InlineData("staleAccount",409)][InlineData("revoked",403)][InlineData("profile",403)]
    [InlineData("expired",403)][InlineData("policy",403)][InlineData("generation",403)]
    public async Task GivenInvalidIdentityOwnerStateOrAccess_WhenChanging_ThenFailWithoutMutatingProtection(string state,int status)
    {
        using var client=new ProtectionFixture();var s=await Setup(client);Authorize(RegistrationApiFixture.Token(s.Actor));var change=Change(s,client);
        if(state=="foreign")change=change with { AccountId=Guid.NewGuid() };
        if(state=="staleProtection")change=change with { ExpectedProtectionRevision=2 };
        if(state=="staleAccount")change=change with { ExpectedAccountRevision=2 };
        var raw=Bytes(change);var c=await Challenge(raw);
        await using(var db=fixture.Context())
        {
            var a=await db.Accounts.SingleAsync(x=>x.PublicId==s.Id);var session=await db.VaultAccessSessions.SingleAsync(x=>x.AccountId==a.Id);
            if(state=="closing")a.State=AccountState.ClosurePending;
            if(state=="erased")db.TerminalErasures.Add(new TerminalErasure { ResourceId=s.Id,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow });
            if(state=="noProtection")await db.VaultProtections.Where(x=>x.AccountId==a.Id).ExecuteDeleteAsync();
            if(state=="missing")await db.Accounts.Where(x=>x.Id==a.Id).ExecuteDeleteAsync();
            if(state=="revoked")session.Revoked=true;if(state=="profile")session.ProfileId=123;if(state=="expired")session.ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1);
            if(state=="policy")session.PolicyRevision=2;if(state=="generation")session.RevocationGeneration=2;
            await db.SaveChangesAsync();
        }
        if(state=="anonymous")Gateway.Client.DefaultRequestHeaders.Authorization=null;
        if(state=="scope")Authorize(RegistrationApiFixture.Token(s.Actor,Guid.NewGuid()));
        if(state is "deletedIdentity" or "identityUnavailable")
        {var pair=fixture.Users.Single(x=>x.Value.Id==s.Actor);fixture.Users[pair.Key]=pair.Value with { Deleted=state=="deletedIdentity",IdentityUnavailable=state=="identityUnavailable" };}
        var before=await Snapshot(s);using var response=await Send(raw,c,client.Sign(c),s.Access);await Failure(response,(HttpStatusCode)status);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory]
    [InlineData("unknown")][InlineData("actor")][InlineData("private")][InlineData("duplicate")][InlineData("case")][InlineData("numericString")]
    [InlineData("missingContent")][InlineData("extraContent")][InlineData("foreignContent")][InlineData("wrongEpoch")][InlineData("keySubstitution")][InlineData("saltReuse")]
    public async Task GivenMalformedOrIncompleteReplacement_WhenChanging_ThenReturn400WithoutConsumption(string invalid)
    {
        using var client=new ProtectionFixture();using var other=new ProtectionFixture();var s=await Setup(client);Authorize(RegistrationApiFixture.Token(s.Actor));var change=Change(s,client,"rotate-content");var item=change.ContentReplacements[0];
        change=invalid switch
        {
            "missingContent"=>change with { ContentReplacements=[] },"extraContent"=>change with { ContentReplacements=[item,item] },
            "foreignContent"=>change with { ContentReplacements=[item with { ResourceId=Guid.NewGuid() }] },
            "wrongEpoch"=>change with { ContentReplacements=[item with { Envelope=item.Envelope with { KeyEpoch=1 } }] },
            "keySubstitution"=>change with { Material=change.Material with { RecipientKey=other.Material.RecipientKey } },
            "saltReuse"=>change with { Material=change.Material with { RecoveryWrapper=change.Material.RecoveryWrapper with { Nonce=client.Material.RecoveryWrapper.Nonce } } },_=>change
        };
        var text=JsonSerializer.Serialize(change,ProtectionFixture.Json);
        text=invalid switch
        {
            "unknown"=>text.Insert(1,"\"password\":\"secret\","),"actor"=>text.Insert(1,"\"actor\":\""+Guid.NewGuid()+"\","),
            "private"=>text.Replace("\"crv\":","\"d\":\"private\",\"crv\":"),"duplicate"=>text.Insert(1,"\"expectedProtectionRevision\":1,"),
            "case"=>text.Replace("\"accountId\":","\"AccountId\":"),"numericString"=>text.Replace("\"expectedProtectionRevision\":1","\"expectedProtectionRevision\":\"1\""),_=>text
        };
        var raw=Encoding.UTF8.GetBytes(text);var c=await Challenge(raw);var before=await Snapshot(s);using var response=await Send(raw,c,client.Sign(c),s.Access);
        await Failure(response,HttpStatusCode.BadRequest);Assert.Equal(before,await Snapshot(s));await Unconsumed(c.ChallengeId);
    }

    [FunctionalTheory]
    [InlineData("missingAccess",401)][InlineData("badAccess",400)][InlineData("multipleAccess",400)]
    [InlineData("missingProof",401)][InlineData("missingChallenge",401)][InlineData("multipleProof",400)][InlineData("badProof",400)]
    [InlineData("badChallenge",400)][InlineData("query",400)][InlineData("gzip",400)][InlineData("bom",400)]
    [InlineData("wrongKey",401)][InlineData("purpose",401)][InlineData("body",401)]
    public async Task GivenInvalidHeadersEncodingOrProofBinding_WhenChanging_ThenWriteNothing(string invalid,int status)
    {
        using var client=new ProtectionFixture();using var other=new ProtectionFixture();var s=await Setup(client);Authorize(RegistrationApiFixture.Token(s.Actor));var raw=Bytes(Change(s,client));
        var c=await Challenge(raw,invalid=="purpose"?"unlock-account":"change-protection");var before=await Snapshot(s);
        if(invalid=="body")raw=Encoding.UTF8.GetBytes(" "+Encoding.UTF8.GetString(raw));if(invalid=="bom")raw=[239,187,191,..raw];
        using var request=new HttpRequestMessage(HttpMethod.Put,"/api/vault/protection"+(invalid=="query"?"?owner=untrusted":"")) { Content=new ByteArrayContent(raw) };
        request.Content.Headers.ContentType=new("application/json");if(invalid=="gzip")request.Content.Headers.ContentEncoding.Add("gzip");
        if(invalid!="missingAccess")request.Headers.Add("X-Cerberus-Vault-Access",invalid=="badAccess"?"bad":s.Access);
        if(invalid!="missingChallenge")request.Headers.Add("X-Cerberus-Challenge-Id",invalid=="badChallenge"?"bad":c.ChallengeId.ToString());
        if(invalid!="missingProof")request.Headers.Add("X-Cerberus-Proof",invalid=="badProof"?"bad":invalid=="wrongKey"?other.Sign(c):client.Sign(c));
        if(invalid=="multipleAccess")request.Headers.Add("X-Cerberus-Vault-Access",s.Access);if(invalid=="multipleProof")request.Headers.Add("X-Cerberus-Proof",client.Sign(c));
        using var response=await Gateway.Client.SendAsync(request);await Failure(response,(HttpStatusCode)status);Assert.Equal(before,await Snapshot(s));await Unconsumed(c.ChallengeId);
    }

    [FunctionalFact]
    public async Task GivenOriginalWhitespaceBody_WhenChanging_ThenVerifyWithoutReserialization()
    {
        using var client=new ProtectionFixture();var s=await Setup(client);Authorize(RegistrationApiFixture.Token(s.Actor));
        var raw=Encoding.UTF8.GetBytes("\n "+JsonSerializer.Serialize(Change(s,client),ProtectionFixture.Json)+"\n");var c=await Challenge(raw);
        using var response=await Send(raw,c,client.Sign(c),s.Access);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenConcurrentHttpChanges_WhenCommitting_ThenOneCompleteWinner()
    {
        using var client=new ProtectionFixture();var s=await Setup(client);Authorize(RegistrationApiFixture.Token(s.Actor));var a=Change(s,client);var b=Change(s,client);
        var ar=Bytes(a);var br=Bytes(b);var ac=await Challenge(ar);var bc=await Challenge(br);
        var responses=await Task.WhenAll(Send(ar,ac,client.Sign(ac),s.Access),Send(br,bc,client.Sign(bc),s.Access));
        try
        {
            Assert.Single(responses,x=>x.StatusCode==HttpStatusCode.OK);Assert.Single(responses,x=>x.StatusCode==HttpStatusCode.Conflict);
            await using var db=fixture.Context();var p=await db.VaultProtections.SingleAsync(x=>db.Accounts.Any(y=>y.Id==x.AccountId&&y.PublicId==s.Id));
            Assert.Equal(responses[0].StatusCode==HttpStatusCode.OK?a.Material:b.Material,JsonSerializer.Deserialize<ProtectionMaterial>(p.Material,ProtectionFixture.Json));
        }
        finally{foreach(var r in responses)r.Dispose();}
    }

    [FunctionalFact]
    public async Task GivenUnavailableProtectionPersistence_WhenChanging_ThenReturn503WithoutMutation()
    {
        using var client=new ProtectionFixture();var s=await Setup(client);Authorize(RegistrationApiFixture.Token(s.Actor));var raw=Bytes(Change(s,client));var c=await Challenge(raw);var before=await Snapshot(s);
        await using var db=fixture.Context();await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.vault_protection RENAME TO fixture_change_unavailable_protection");
        try{using var response=await Send(raw,c,client.Sign(c),s.Access);await Failure(response,HttpStatusCode.ServiceUnavailable);}
        finally{await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_change_unavailable_protection RENAME TO vault_protection");}
        Assert.Equal(before,await Snapshot(s));await Unconsumed(c.ChallengeId);
    }

    private sealed record State(Guid Actor,Guid Id,string Access,byte[] Envelope);
    private async Task<State> Setup(ProtectionFixture client,bool initialize=true)
    {
        var actor=Guid.NewGuid();var id=Guid.NewGuid();fixture.Users[Guid.NewGuid().ToString("N")+"@example.test"]=new(actor,"fixture-password");
        var envelope=Bytes(new EncryptedEnvelope("cerberus-content-v1",1,ProtectionFixture.Encode(new byte[32]),ProtectionFixture.Encode(new byte[12]),"AQID",ProtectionFixture.Encode(new byte[16])));
        var access=ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32));OpaqueAccessHandle.TryHash(access,out var verifier);
        await using var db=fixture.Context();var a=new Account { PublicId=id,HeimdallPublicId=actor,DetailsEnvelope=envelope,Revision=3 };db.Accounts.Add(a);await db.SaveChangesAsync();
        if(initialize)
        {
            db.VaultProtections.Add(new VaultProtection { AccountId=a.Id,Material=Bytes(client.Material) });
            db.VaultAccessSessions.Add(new VaultAccessSession { AccountId=a.Id,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddSeconds(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1 });await db.SaveChangesAsync();
        }
        return new(actor,id,access,envelope);
    }
    private static ProtectionChange Change(State s,ProtectionFixture client,string mode="rewrap")=>new(s.Id,1,3,mode,client.Rewrap(),mode=="rewrap"?[]:
        [new("account",s.Id,3,new("cerberus-content-v1",2,ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32)),ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(12)),"BAUG",ProtectionFixture.Encode(new byte[16])))]);
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    private async Task<VaultProofChallenge> Challenge(byte[] raw,string operation="change-protection")
    {
        using var response=await Gateway.Client.PostAsJsonAsync("/api/vault/challenges",new { operation,requestHash=ProtocolBinary.Encode(SHA256.HashData(raw)) });
        Assert.Equal(HttpStatusCode.Created,response.StatusCode);return (await response.Content.ReadFromJsonAsync<DataOutput<VaultChallengeOutput>>())!.Data!.Challenge;
    }
    private async Task<string> Unlock(ProtectionFixture client,long revision)
    {
        var raw=Bytes(new { expectedProtectionRevision=revision });var c=await Challenge(raw,"unlock-account");
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/vault/unlock") { Content=new ByteArrayContent(raw) };request.Content.Headers.ContentType=new("application/json");request.Headers.Add("X-Cerberus-Challenge-Id",c.ChallengeId.ToString());request.Headers.Add("X-Cerberus-Proof",client.Sign(c));
        using var response=await Gateway.Client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);return (await response.Content.ReadFromJsonAsync<DataOutput<VaultUnlockOutput>>())!.Data!.VaultAccess;
    }
    private async Task<HttpResponseMessage> Send(byte[] raw,VaultProofChallenge c,string proof,string access)
    {
        using var request=new HttpRequestMessage(HttpMethod.Put,"/api/vault/protection") { Content=new ByteArrayContent(raw) };request.Content.Headers.ContentType=new("application/json");request.Headers.Add("X-Cerberus-Vault-Access",access);request.Headers.Add("X-Cerberus-Challenge-Id",c.ChallengeId.ToString());request.Headers.Add("X-Cerberus-Proof",proof);return await Gateway.Client.SendAsync(request);
    }
    private async Task<HttpResponseMessage> Read(string access)
    {using var r=new HttpRequestMessage(HttpMethod.Get,"/api/accounts/me");r.Headers.Add("X-Cerberus-Vault-Access",access);return await Gateway.Client.SendAsync(r);}
    private async Task Unconsumed(Guid id)
    {await using var db=fixture.Context();Assert.False((await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==id)).Consumed);}
    private async Task<string> Snapshot(State s)
    {
        await using var db=fixture.Context();var a=await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.PublicId==s.Id);
        return JsonSerializer.Serialize(new { Account=a,Protection=a is null?null:await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==a.Id),Sessions=a is null?[]:await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==a.Id).ToListAsync() });
    }
    private static async Task Failure(HttpResponseMessage r,HttpStatusCode status)
    {Assert.Equal(status,r.StatusCode);Assert.True(r.Headers.CacheControl?.NoStore);using var doc=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Assert.False(doc.RootElement.GetProperty("success").GetBoolean());if(doc.RootElement.TryGetProperty("data",out var data))Assert.Equal(JsonValueKind.Null,data.ValueKind);}
}
