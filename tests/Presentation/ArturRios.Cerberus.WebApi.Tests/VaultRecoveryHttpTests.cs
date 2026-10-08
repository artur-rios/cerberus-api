using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Command.Protection;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Configuration.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class VaultRecoveryHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenFreshOwnerWithoutSession_WhenRecoveringAndRetrying_ThenCommitOnceAndRetainHistoricalOutcome()
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();using var third=new ProtectionFixture();var s=await Setup(old,false);Authorize(RegistrationApiFixture.Token(s.Actor));
        using(var init=await Gateway.Client.PostAsJsonAsync("/api/vault/protection",new { accountId=s.Id,expectedAccountRevision=3,material=old.Material }))Assert.Equal(HttpStatusCode.Created,init.StatusCode);
        var input=Replacement(old,next);var raw=Bytes(input);var c=await Challenge(raw);Assert.Equal(1,c.Generation);var calls=fixture.IdentityCalls;
        using(var response=await Send(raw,c,old.SignRecovery(c)))
        {
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);var outcome=(await response.Content.ReadFromJsonAsync<DataOutput<RecoverVaultOutput>>())!.Data!;
            Assert.Equal("committed",outcome.Status);Assert.Equal(2,outcome.Generation);Assert.Equal(2,outcome.ProtectionRevision);Assert.Equal(2,outcome.RevocationGeneration);
            using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal(4,doc.RootElement.GetProperty("data").EnumerateObject().Count());
        }
        Assert.True(fixture.IdentityCalls>calls);
        var second=Replacement(old,third,3,3,2);var secondRaw=Bytes(second);var secondChallenge=await Challenge(secondRaw);
        using(var denied=await Send(secondRaw,secondChallenge,old.SignRecovery(secondChallenge)))await Failure(denied,401);
        using(var committed=await Send(secondRaw,secondChallenge,next.SignRecovery(secondChallenge)))Assert.Equal(HttpStatusCode.OK,committed.StatusCode);
        var before=await Snapshot(s);using(var retry=await Send(raw,c,old.SignRecovery(c)))
        {Assert.Equal(HttpStatusCode.OK,retry.StatusCode);Assert.Equal(2,(await retry.Content.ReadFromJsonAsync<DataOutput<RecoverVaultOutput>>())!.Data!.Generation);}
        Assert.Equal(before,await Snapshot(s));using(var conflict=await Send([..raw,(byte)' '],c,old.SignRecovery(c)))await Failure(conflict,409);Assert.Equal(before,await Snapshot(s));
        await using var db=fixture.Context();var account=await db.Accounts.SingleAsync(x=>x.PublicId==s.Id);Assert.Equal(s.Envelope,account.DetailsEnvelope);Assert.Equal(3,account.Revision);Assert.Equal(3,account.RevocationGeneration);
        Assert.Empty(await db.VaultAccessSessions.Where(x=>x.AccountId==account.Id).ToListAsync());Assert.Equal(2,await db.VaultRecoveryOperations.CountAsync(x=>x.AccountId==account.Id));
        Assert.Equal(second.Replace(input.Replace(old.Material)),JsonSerializer.Deserialize<ProtectionMaterial>((await db.VaultProtections.SingleAsync(x=>x.AccountId==account.Id)).Material,ProtectionFixture.Json));
    }
    [FunctionalTheory]
    [InlineData("anonymous",401)][InlineData("scope",401)][InlineData("deletedIdentity",401)][InlineData("identityUnavailable",503)]
    [InlineData("foreign",404)][InlineData("closing",404)][InlineData("erased",404)][InlineData("noProtection",404)]
    [InlineData("revision",409)][InlineData("policy",409)][InlineData("consumed",401)][InlineData("expired",401)]
    public async Task GivenInvalidIdentityOrCurrentState_WhenRecovering_ThenPreserveEveryWrite(string state,int status)
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);Authorize(RegistrationApiFixture.Token(s.Actor));var input=Replacement(old,next);if(state=="revision")input=input with { ExpectedRevision=2 };var raw=Bytes(input);var c=await Challenge(raw);
        await using(var db=fixture.Context())
        {
            var a=await db.Accounts.SingleAsync(x=>x.PublicId==s.Id);if(state=="closing")a.State=AccountState.ClosurePending;if(state=="policy")a.PolicyRevision++;
            if(state=="erased")db.TerminalErasures.Add(new TerminalErasure { ResourceId=s.Id,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow });
            if(state=="noProtection")await db.VaultProtections.Where(x=>x.AccountId==a.Id).ExecuteDeleteAsync();
            var row=await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==c.ChallengeId);if(state=="consumed")row.Consumed=true;
            if(state=="expired"){c=c with { IssuedAt=1,ExpiresAt=61 };row.Challenge=Bytes(c);row.ExpiresAt=DateTimeOffset.FromUnixTimeSeconds(61);}await db.SaveChangesAsync();
        }
        if(state=="anonymous")Gateway.Client.DefaultRequestHeaders.Authorization=null;if(state=="scope")Authorize(RegistrationApiFixture.Token(s.Actor,Guid.NewGuid()));if(state=="foreign"){var foreign=Guid.NewGuid();fixture.Users[Guid.NewGuid()+"@example.test"]=new(foreign,"fixture-password");Authorize(RegistrationApiFixture.Token(foreign));}
        if(state is "deletedIdentity" or "identityUnavailable"){var pair=fixture.Users.Single(x=>x.Value.Id==s.Actor);fixture.Users[pair.Key]=pair.Value with { Deleted=state=="deletedIdentity",IdentityUnavailable=state=="identityUnavailable" };}
        var before=await Snapshot(s);using var response=await Send(raw,c,old.SignRecovery(c));await Failure(response,status);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory]
    [InlineData("unknown")][InlineData("actor")][InlineData("private")][InlineData("duplicate")][InlineData("case")][InlineData("numericString")]
    [InlineData("missing")][InlineData("generation")][InlineData("reusedKey")][InlineData("salt")][InlineData("operation")][InlineData("key")]
    public async Task GivenMalformedOrIncompleteRecovery_WhenSubmitting_ThenReturn400Unchanged(string invalid)
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);Authorize(RegistrationApiFixture.Token(s.Actor));var p=Replacement(old,next);
        p=invalid switch { "generation"=>p with { RecoveryWrapper=p.RecoveryWrapper with { Generation=3 } },"reusedKey"=>p with { NewRecoveryVerifier=old.Material.UnlockVerifier,RecoveryWrapper=p.RecoveryWrapper with { ProofKeyFingerprint=old.Material.UnlockVerifier.Fingerprint() } },"salt"=>p with { PasswordWrapper=p.PasswordWrapper with { Nonce=old.Material.PasswordWrapper.Nonce } },"operation"=>p with { Operation="refresh-recovery" },"key"=>p with { IdempotencyKey=Guid.Empty },_=>p };
        var text=JsonSerializer.Serialize(p,ProtectionFixture.Json);text=invalid switch { "unknown"=>text.Insert(1,"\"password\":\"secret\","),"actor"=>text.Insert(1,"\"actor\":\""+s.Actor+"\","),"private"=>text.Replace("\"crv\":","\"d\":\"private\",\"crv\":"),"duplicate"=>text.Insert(1,"\"expectedRevision\":1,"),"case"=>text.Replace("\"operation\":","\"Operation\":"),"numericString"=>text.Replace("\"expectedRevision\":1","\"expectedRevision\":\"1\""),"missing"=>text.Replace("\"newRecoveryVerifier\":", "\"missingVerifier\":"),_=>text };
        var raw=Encoding.UTF8.GetBytes(text);var c=await Challenge(raw);var before=await Snapshot(s);using var response=await Send(raw,c,old.SignRecovery(c));await Failure(response,400);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory]
    [InlineData("missingProof",401)][InlineData("missingChallenge",401)][InlineData("multipleProof",400)][InlineData("multipleChallenge",400)]
    [InlineData("badProof",400)][InlineData("badChallenge",400)][InlineData("query",400)][InlineData("gzip",400)][InlineData("bom",400)]
    [InlineData("wrongKey",401)][InlineData("unlockKey",401)][InlineData("newKey",401)][InlineData("purpose",401)][InlineData("body",401)]
    public async Task GivenInvalidWireOrProof_WhenRecovering_ThenReturnFailureUnchanged(string invalid,int status)
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();using var other=new ProtectionFixture();var s=await Setup(old);Authorize(RegistrationApiFixture.Token(s.Actor));var raw=Bytes(Replacement(old,next));var c=await Challenge(raw,invalid=="purpose"?"unlock-account":"recover");var before=await Snapshot(s);
        if(invalid=="body")raw=[(byte)' ',..raw];if(invalid=="bom")raw=[239,187,191,..raw];using var request=new HttpRequestMessage(HttpMethod.Post,"/api/vault/recovery"+(invalid=="query"?"?owner=untrusted":"")) { Content=new ByteArrayContent(raw) };request.Content.Headers.ContentType=new("application/json");
        if(invalid=="gzip")request.Content.Headers.ContentEncoding.Add("gzip");if(invalid!="missingChallenge")request.Headers.Add("X-Cerberus-Challenge-Id",invalid=="badChallenge"?"bad":c.ChallengeId.ToString());
        if(invalid!="missingProof")request.Headers.Add("X-Cerberus-Proof",invalid=="badProof"?"bad":invalid=="wrongKey"?other.SignRecovery(c):invalid=="unlockKey"?old.Sign(c):invalid=="newKey"?next.SignRecovery(c):old.SignRecovery(c));
        if(invalid=="multipleProof")request.Headers.Add("X-Cerberus-Proof",old.SignRecovery(c));if(invalid=="multipleChallenge")request.Headers.Add("X-Cerberus-Challenge-Id",c.ChallengeId.ToString());
        using var response=await Gateway.Client.SendAsync(request);await Failure(response,status);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenWhitespaceOriginalBodyAndConcurrentExactRetry_WhenRecovering_ThenOnlyOneTransition()
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);Authorize(RegistrationApiFixture.Token(s.Actor));var raw=Encoding.UTF8.GetBytes("\n "+JsonSerializer.Serialize(Replacement(old,next),ProtectionFixture.Json)+"\n");var c=await Challenge(raw);
        var responses=await Task.WhenAll(Send(raw,c,old.SignRecovery(c)),Send(raw,c,old.SignRecovery(c)));try{Assert.All(responses,r=>Assert.Equal(HttpStatusCode.OK,r.StatusCode));await using var db=fixture.Context();var a=await db.Accounts.SingleAsync(x=>x.PublicId==s.Id);Assert.Equal(2,a.RevocationGeneration);Assert.Equal(1,await db.VaultRecoveryOperations.CountAsync(x=>x.AccountId==a.Id));}finally{foreach(var response in responses)response.Dispose();}
    }
    [FunctionalFact]
    public async Task GivenUnavailableRecoveryPersistence_WhenRecovering_ThenReturn503WithoutMutation()
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);Authorize(RegistrationApiFixture.Token(s.Actor));var raw=Bytes(Replacement(old,next));var c=await Challenge(raw);var before=await Snapshot(s);await using var db=fixture.Context();await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.vault_recovery_operation RENAME TO fixture_unavailable_recovery");
        try{using var response=await Send(raw,c,old.SignRecovery(c));await Failure(response,503);}finally{await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_recovery RENAME TO vault_recovery_operation");}Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory]
    [InlineData("missing")][InlineData("old")][InlineData("future")]
    public async Task GivenUnfreshSignedIdentity_WhenRecoveringOrReplaying_ThenRejectUnchanged(string freshness)
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);Authorize(RegistrationApiFixture.Token(s.Actor));var raw=Bytes(Replacement(old,next));var c=await Challenge(raw);
        var before=await Snapshot(s);Authorize(TokenWithIssuedAt(s.Actor,freshness));using(var response=await Send(raw,c,old.SignRecovery(c)))await Failure(response,401);Assert.Equal(before,await Snapshot(s));
        Authorize(RegistrationApiFixture.Token(s.Actor));using(var committed=await Send(raw,c,old.SignRecovery(c)))Assert.Equal(HttpStatusCode.OK,committed.StatusCode);
        before=await Snapshot(s);Authorize(TokenWithIssuedAt(s.Actor,freshness));using(var retry=await Send(raw,c,old.SignRecovery(c)))await Failure(retry,401);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenCommittedRecoveryAndDeletedProviderIdentity_WhenReplaying_ThenRequireCurrentIdentity()
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);Authorize(RegistrationApiFixture.Token(s.Actor));var raw=Bytes(Replacement(old,next));var c=await Challenge(raw);
        using(var committed=await Send(raw,c,old.SignRecovery(c)))Assert.Equal(HttpStatusCode.OK,committed.StatusCode);
        var pair=fixture.Users.Single(x=>x.Value.Id==s.Actor);fixture.Users[pair.Key]=pair.Value with { Deleted=true };var before=await Snapshot(s);using var retry=await Send(raw,c,old.SignRecovery(c));await Failure(retry,401);Assert.Equal(before,await Snapshot(s));
    }
    private static string TokenWithIssuedAt(Guid actor,string freshness)
    {
        var claims=new List<Claim> { new("id",actor.ToString()),new("roleId","3"),new("scopeId",RegistrationApiFixture.Scope.ToString()) };
        if(freshness!="missing")claims.Add(new("iat",(DateTimeOffset.UtcNow.ToUnixTimeSeconds()+(freshness=="old"?-120:120)).ToString(),ClaimValueTypes.Integer64));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("fixture-issuer","fixture-audience",claims,DateTime.UtcNow.AddMinutes(-5),DateTime.UtcNow.AddMinutes(5),new SigningCredentials(new SymmetricSecurityKey(Encoding.ASCII.GetBytes("fixture-only-signing-key-32-characters")),SecurityAlgorithms.HmacSha256)));
    }
    private sealed record State(Guid Actor,Guid Id,byte[] Envelope);
    private async Task<State> Setup(ProtectionFixture old,bool initialize=true)
    {
        var actor=Guid.NewGuid();var id=Guid.NewGuid();fixture.Users[Guid.NewGuid().ToString("N")+"@example.test"]=new(actor,"fixture-password");var envelope=Bytes(new EncryptedEnvelope("cerberus-content-v1",1,ProtectionFixture.Encode(new byte[32]),ProtectionFixture.Encode(new byte[12]),"AQID",ProtectionFixture.Encode(new byte[16])));
        await using var db=fixture.Context();var a=new Account { PublicId=id,HeimdallPublicId=actor,DetailsEnvelope=envelope,Revision=3 };db.Accounts.Add(a);await db.SaveChangesAsync();if(initialize){db.VaultProtections.Add(new VaultProtection { AccountId=a.Id,Material=Bytes(old.Material) });await db.SaveChangesAsync();}return new(actor,id,envelope);
    }
    private static RecoveryReplacement Replacement(ProtectionFixture old,ProtectionFixture next,long epoch=2,long generation=2,long revision=1)
    {var m=old.Rewrap(epoch);return new("recover",Guid.NewGuid(),revision,m.PasswordWrapper,m.RecoveryWrapper with { Generation=generation,ProofKeyFingerprint=next.Material.RecoveryVerifier.Fingerprint() },next.Material.RecoveryVerifier);}
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    private async Task<VaultProofChallenge> Challenge(byte[] raw,string operation="recover")
    {using var response=await Gateway.Client.PostAsJsonAsync("/api/vault/challenges",new { operation,requestHash=ProtocolBinary.Encode(SHA256.HashData(raw)) });Assert.Equal(HttpStatusCode.Created,response.StatusCode);return(await response.Content.ReadFromJsonAsync<DataOutput<VaultChallengeOutput>>())!.Data!.Challenge;}
    private async Task<HttpResponseMessage> Send(byte[] raw,VaultProofChallenge c,string proof)
    {using var request=new HttpRequestMessage(HttpMethod.Post,"/api/vault/recovery") { Content=new ByteArrayContent(raw) };request.Content.Headers.ContentType=new("application/json");request.Headers.Add("X-Cerberus-Challenge-Id",c.ChallengeId.ToString());request.Headers.Add("X-Cerberus-Proof",proof);return await Gateway.Client.SendAsync(request);}
    private async Task<string> Snapshot(State s)
    {await using var db=fixture.Context();var a=await db.Accounts.AsNoTracking().SingleAsync(x=>x.PublicId==s.Id);return JsonSerializer.Serialize(new { Account=a,Protection=await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==a.Id),Challenges=await db.VaultUnlockChallenges.AsNoTracking().Where(x=>x.AccountId==a.Id).OrderBy(x=>x.Id).ToListAsync(),Outcomes=await db.VaultRecoveryOperations.AsNoTracking().Where(x=>x.AccountId==a.Id).OrderBy(x=>x.Id).ToListAsync() });}
    private static async Task Failure(HttpResponseMessage response,int status)
    {Assert.Equal((HttpStatusCode)status,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.False(doc.RootElement.GetProperty("success").GetBoolean());if(doc.RootElement.TryGetProperty("data",out var data))Assert.Equal(JsonValueKind.Null,data.ValueKind);}
}
