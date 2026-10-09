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
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Configuration.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class VaultRecoveryRefreshHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenInitializedUnlockedOwner_WhenRefreshing_ThenOldRecoveryFailsAndNewUnlockAndRecoveryWork()
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();using var third=new ProtectionFixture();var state=await Setup(old,false);Authorize(RegistrationApiFixture.Token(state.Actor));
        using(var init=await Gateway.Client.PostAsJsonAsync("/api/vault/protection",new { accountId=state.Id,expectedAccountRevision=3,material=old.Material }))Assert.Equal(HttpStatusCode.Created,init.StatusCode);
        currentAccess=await Unlock(old,1);var oldRecovery=Replacement(old,third) with { Operation="recover" };var oldRaw=Bytes(oldRecovery);var oldChallenge=await Challenge(oldRaw,"recover");
        var input=Replacement(old,next);var raw=Bytes(input);var c=await Challenge(raw);Assert.Null(c.Generation);var originalAccess=currentAccess;
        using(var response=await Send(raw,c,old.Sign(c)))
        {Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);var result=(await response.Content.ReadFromJsonAsync<DataOutput<RecoverVaultOutput>>())!.Data!;Assert.Equal(new[]{2L,2L,2L},new[]{result.ProtectionRevision,result.Generation,result.RevocationGeneration});Assert.Equal("committed",result.Status);}
        var before=await Snapshot(state);using(var rejected=await Recover(oldRaw,oldChallenge,old.SignRecovery(oldChallenge)))await Failure(rejected,409);Assert.Equal(before,await Snapshot(state));
        using(var retry=await Send(raw,c,old.Sign(c),originalAccess))Assert.Equal(HttpStatusCode.OK,retry.StatusCode);Assert.Equal(before,await Snapshot(state));
        currentAccess=await Unlock(old,2);var recovery=Replacement(old,third,3,3,2) with { Operation="recover" };var recoveryRaw=Bytes(recovery);var recoveryChallenge=await Challenge(recoveryRaw,"recover");
        using(var rejected=await Recover(recoveryRaw,recoveryChallenge,old.SignRecovery(recoveryChallenge)))await Failure(rejected,401);
        using(var committed=await Recover(recoveryRaw,recoveryChallenge,next.SignRecovery(recoveryChallenge)))Assert.Equal(HttpStatusCode.OK,committed.StatusCode);
        before=await Snapshot(state);using(var replay=await Send(raw,c,old.Sign(c),originalAccess)){Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.Equal(2,(await replay.Content.ReadFromJsonAsync<DataOutput<RecoverVaultOutput>>())!.Data!.Generation);}Assert.Equal(before,await Snapshot(state));
        using(var conflict=await Send([..raw,(byte)' '],c,old.Sign(c),originalAccess))await Failure(conflict,409);Assert.Equal(before,await Snapshot(state));
        await using var db=fixture.Context();var account=await db.Accounts.SingleAsync(x=>x.PublicId==state.Id);Assert.Equal(state.Envelope,account.DetailsEnvelope);Assert.Equal(3,account.Revision);Assert.Equal(3,account.RevocationGeneration);Assert.Equal(2,await db.VaultRecoveryOperations.CountAsync(x=>x.AccountId==account.Id));
    }
    [FunctionalTheory]
    [InlineData("anonymous",401)][InlineData("scope",401)][InlineData("deletedIdentity",401)][InlineData("identityUnavailable",503)]
    [InlineData("foreign",404)][InlineData("closing",404)][InlineData("erased",404)][InlineData("noProtection",404)]
    [InlineData("revision",409)][InlineData("policy",403)][InlineData("challengePolicy",409)]
    [InlineData("revokedSession",403)][InlineData("expiredSession",403)][InlineData("profileSession",403)][InlineData("accessGeneration",403)][InlineData("consumed",401)][InlineData("expired",401)]
    public async Task GivenInvalidIdentityOrCurrentState_WhenRecovering_ThenPreserveEveryWrite(string state,int status)
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);Authorize(RegistrationApiFixture.Token(s.Actor));var input=Replacement(old,next);if(state=="revision")input=input with { ExpectedRevision=2 };var raw=Bytes(input);var c=await Challenge(raw);
        await using(var db=fixture.Context())
        {
            var a=await db.Accounts.SingleAsync(x=>x.PublicId==s.Id);if(state=="closing")a.State=AccountState.ClosurePending;if(state=="policy")a.PolicyRevision++;
            if(state=="erased")db.TerminalErasures.Add(new TerminalErasure { ResourceId=s.Id,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow });
            if(state=="noProtection")await db.VaultProtections.Where(x=>x.AccountId==a.Id).ExecuteDeleteAsync();
            var session=await db.VaultAccessSessions.SingleAsync(x=>x.AccountId==a.Id);if(state=="revokedSession")session.Revoked=true;if(state=="expiredSession")session.ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1);if(state=="profileSession")session.ProfileId=123;if(state=="accessGeneration")session.RevocationGeneration++;
            var row=await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==c.ChallengeId);if(state=="challengePolicy")row.PolicyRevision++;if(state=="consumed")row.Consumed=true;
            if(state=="expired"){c=c with { IssuedAt=1,ExpiresAt=61 };row.Challenge=Bytes(c);row.ExpiresAt=DateTimeOffset.FromUnixTimeSeconds(61);}await db.SaveChangesAsync();
        }
        if(state=="anonymous")Gateway.Client.DefaultRequestHeaders.Authorization=null;if(state=="scope")Authorize(RegistrationApiFixture.Token(s.Actor,Guid.NewGuid()));if(state=="foreign"){var foreign=Guid.NewGuid();fixture.Users[Guid.NewGuid()+"@example.test"]=new(foreign,"fixture-password");Authorize(RegistrationApiFixture.Token(foreign));}
        if(state is "deletedIdentity" or "identityUnavailable"){var pair=fixture.Users.Single(x=>x.Value.Id==s.Actor);fixture.Users[pair.Key]=pair.Value with { Deleted=state=="deletedIdentity",IdentityUnavailable=state=="identityUnavailable" };}
        var before=await Snapshot(s);using var response=await Send(raw,c,old.Sign(c));await Failure(response,status);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory]
    [InlineData("unknown")][InlineData("actor")][InlineData("private")][InlineData("duplicate")][InlineData("case")][InlineData("numericString")]
    [InlineData("missing")][InlineData("generation")][InlineData("reusedKey")][InlineData("salt")][InlineData("operation")][InlineData("key")]
    public async Task GivenMalformedOrIncompleteRecovery_WhenSubmitting_ThenReturn400Unchanged(string invalid)
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);Authorize(RegistrationApiFixture.Token(s.Actor));var p=Replacement(old,next);
        p=invalid switch { "generation"=>p with { RecoveryWrapper=p.RecoveryWrapper with { Generation=3 } },"reusedKey"=>p with { NewRecoveryVerifier=old.Material.UnlockVerifier,RecoveryWrapper=p.RecoveryWrapper with { ProofKeyFingerprint=old.Material.UnlockVerifier.Fingerprint() } },"salt"=>p with { PasswordWrapper=p.PasswordWrapper with { Nonce=old.Material.PasswordWrapper.Nonce } },"operation"=>p with { Operation="recover" },"key"=>p with { IdempotencyKey=Guid.Empty },_=>p };
        var text=JsonSerializer.Serialize(p,ProtectionFixture.Json);text=invalid switch { "unknown"=>text.Insert(1,"\"password\":\"secret\","),"actor"=>text.Insert(1,"\"actor\":\""+s.Actor+"\","),"private"=>text.Replace("\"crv\":","\"d\":\"private\",\"crv\":"),"duplicate"=>text.Insert(1,"\"expectedRevision\":1,"),"case"=>text.Replace("\"operation\":","\"Operation\":"),"numericString"=>text.Replace("\"expectedRevision\":1","\"expectedRevision\":\"1\""),"missing"=>text.Replace("\"newRecoveryVerifier\":", "\"missingVerifier\":"),_=>text };
        var raw=Encoding.UTF8.GetBytes(text);var c=await Challenge(raw);var before=await Snapshot(s);using var response=await Send(raw,c,old.Sign(c));await Failure(response,400);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory]
    [InlineData("missingAccess",401)][InlineData("badAccess",400)][InlineData("multipleAccess",400)]
    [InlineData("missingProof",401)][InlineData("missingChallenge",401)][InlineData("multipleProof",400)][InlineData("multipleChallenge",400)]
    [InlineData("badProof",400)][InlineData("badChallenge",400)][InlineData("query",400)][InlineData("gzip",400)][InlineData("bom",400)]
    [InlineData("wrongKey",401)][InlineData("recoveryKey",401)][InlineData("newKey",401)][InlineData("purpose",401)][InlineData("body",401)]
    public async Task GivenInvalidWireOrProof_WhenRecovering_ThenReturnFailureUnchanged(string invalid,int status)
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();using var other=new ProtectionFixture();var s=await Setup(old);Authorize(RegistrationApiFixture.Token(s.Actor));var raw=Bytes(Replacement(old,next));var c=await Challenge(raw,invalid=="purpose"?"unlock-account":"refresh-recovery");var before=await Snapshot(s);
        if(invalid=="body")raw=[(byte)' ',..raw];if(invalid=="bom")raw=[239,187,191,..raw];using var request=new HttpRequestMessage(HttpMethod.Put,"/api/vault/recovery"+(invalid=="query"?"?owner=untrusted":"")) { Content=new ByteArrayContent(raw) };request.Content.Headers.ContentType=new("application/json");
        if(invalid!="missingAccess")request.Headers.Add("X-Cerberus-Vault-Access",invalid=="badAccess"?"bad":currentAccess);if(invalid=="multipleAccess")request.Headers.Add("X-Cerberus-Vault-Access",currentAccess);
        if(invalid=="gzip")request.Content.Headers.ContentEncoding.Add("gzip");if(invalid!="missingChallenge")request.Headers.Add("X-Cerberus-Challenge-Id",invalid=="badChallenge"?"bad":c.ChallengeId.ToString());
        if(invalid!="missingProof")request.Headers.Add("X-Cerberus-Proof",invalid=="badProof"?"bad":invalid=="wrongKey"?other.Sign(c):invalid=="recoveryKey"?old.SignRecovery(c):invalid=="newKey"?next.Sign(c):old.Sign(c));
        if(invalid=="multipleProof")request.Headers.Add("X-Cerberus-Proof",old.Sign(c));if(invalid=="multipleChallenge")request.Headers.Add("X-Cerberus-Challenge-Id",c.ChallengeId.ToString());
        using var response=await Gateway.Client.SendAsync(request);await Failure(response,status);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenWhitespaceOriginalBodyAndConcurrentExactRetry_WhenRecovering_ThenOnlyOneTransition()
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);Authorize(RegistrationApiFixture.Token(s.Actor));var raw=Encoding.UTF8.GetBytes("\n "+JsonSerializer.Serialize(Replacement(old,next),ProtectionFixture.Json)+"\n");var c=await Challenge(raw);
        var responses=await Task.WhenAll(Send(raw,c,old.Sign(c)),Send(raw,c,old.Sign(c)));try{Assert.All(responses,r=>Assert.Equal(HttpStatusCode.OK,r.StatusCode));await using var db=fixture.Context();var a=await db.Accounts.SingleAsync(x=>x.PublicId==s.Id);Assert.Equal(2,a.RevocationGeneration);Assert.Equal(1,await db.VaultRecoveryOperations.CountAsync(x=>x.AccountId==a.Id));}finally{foreach(var response in responses)response.Dispose();}
    }
    [FunctionalFact]
    public async Task GivenUnavailableRecoveryPersistence_WhenRecovering_ThenReturn503WithoutMutation()
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);Authorize(RegistrationApiFixture.Token(s.Actor));var raw=Bytes(Replacement(old,next));var c=await Challenge(raw);var before=await Snapshot(s);await using var db=fixture.Context();await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.vault_recovery_operation RENAME TO fixture_unavailable_recovery");
        try{using var response=await Send(raw,c,old.Sign(c));await Failure(response,503);}finally{await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_recovery RENAME TO vault_recovery_operation");}Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory]
    [InlineData("missing")][InlineData("old")][InlineData("future")]
    public async Task GivenUnfreshSignedIdentity_WhenRecoveringOrReplaying_ThenRejectUnchanged(string freshness)
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);Authorize(RegistrationApiFixture.Token(s.Actor));var raw=Bytes(Replacement(old,next));var c=await Challenge(raw);
        var before=await Snapshot(s);Authorize(TokenWithIssuedAt(s.Actor,freshness));using(var response=await Send(raw,c,old.Sign(c)))await Failure(response,401);Assert.Equal(before,await Snapshot(s));
        Authorize(RegistrationApiFixture.Token(s.Actor));using(var committed=await Send(raw,c,old.Sign(c)))Assert.Equal(HttpStatusCode.OK,committed.StatusCode);
        before=await Snapshot(s);Authorize(TokenWithIssuedAt(s.Actor,freshness));using(var retry=await Send(raw,c,old.Sign(c)))await Failure(retry,401);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenCommittedRecoveryAndDeletedProviderIdentity_WhenReplaying_ThenRequireCurrentIdentity()
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);Authorize(RegistrationApiFixture.Token(s.Actor));var raw=Bytes(Replacement(old,next));var c=await Challenge(raw);
        using(var committed=await Send(raw,c,old.Sign(c)))Assert.Equal(HttpStatusCode.OK,committed.StatusCode);
        var pair=fixture.Users.Single(x=>x.Value.Id==s.Actor);fixture.Users[pair.Key]=pair.Value with { Deleted=true };var before=await Snapshot(s);using var retry=await Send(raw,c,old.Sign(c));await Failure(retry,401);Assert.Equal(before,await Snapshot(s));
    }
    private static string TokenWithIssuedAt(Guid actor,string freshness)
    {
        var claims=new List<Claim> { new("id",actor.ToString()),new("roleId","3"),new("scopeId",RegistrationApiFixture.Scope.ToString()) };
        if(freshness!="missing")claims.Add(new("iat",(DateTimeOffset.UtcNow.ToUnixTimeSeconds()+(freshness=="old"?-120:120)).ToString(),ClaimValueTypes.Integer64));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("fixture-issuer","fixture-audience",claims,DateTime.UtcNow.AddMinutes(-5),DateTime.UtcNow.AddMinutes(5),new SigningCredentials(new SymmetricSecurityKey(Encoding.ASCII.GetBytes("fixture-only-signing-key-32-characters")),SecurityAlgorithms.HmacSha256)));
    }
    [FunctionalTheory]
    [InlineData("old")][InlineData("missing")][InlineData("future")]
    public async Task GivenRecoveryMaterialRead_WhenIdentityUnfresh_ThenReturn401WithoutMutation(string freshness)
    {
        using var old=new ProtectionFixture();var state=await Setup(old);Authorize(TokenWithIssuedAt(state.Actor,freshness));var before=await Snapshot(state);
        using var response=await Gateway.Client.GetAsync("/api/vault/recovery-material");await Failure(response,401);Assert.Equal(before,await Snapshot(state));
    }
    [FunctionalTheory]
    [InlineData("valid",200)][InlineData("query",400)][InlineData("body",400)][InlineData("closing",404)][InlineData("missing",404)][InlineData("provider",503)]
    public async Task GivenNamedFreshRecoveryMaterialRead_WhenRequesting_ThenReturnOwnOpaqueMaterialWithoutConsumption(string state,int status)
    {
        using var old=new ProtectionFixture();var s=await Setup(old);Authorize(RegistrationApiFixture.Token(s.Actor));
        if(state is "closing" or "missing"){await using var db=fixture.Context();var a=await db.Accounts.SingleAsync(x=>x.PublicId==s.Id);if(state=="closing"){a.State=AccountState.ClosurePending;await db.SaveChangesAsync();}else await db.VaultProtections.Where(x=>x.AccountId==a.Id).ExecuteDeleteAsync();}
        if(state=="valid"){await using var db=fixture.Context();await db.VaultAccessSessions.Where(x=>db.Accounts.Any(a=>a.Id==x.AccountId&&a.PublicId==s.Id)).ExecuteDeleteAsync();}
        if(state=="provider"){var pair=fixture.Users.Single(x=>x.Value.Id==s.Actor);fixture.Users[pair.Key]=pair.Value with { IdentityUnavailable=true };}
        var before=await Snapshot(s);using var request=new HttpRequestMessage(HttpMethod.Get,"/api/vault/recovery-material"+(state=="query"?"?owner=untrusted":""));if(state=="body")request.Content=new StringContent("{}",Encoding.UTF8,"application/json");
        using var response=await Gateway.Client.SendAsync(request);if(status==200){Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var material=JsonSerializer.Deserialize<ProtectionMaterial>(doc.RootElement.GetProperty("data").GetProperty("material"),ProtectionFixture.Json);Assert.Equal(old.Material,material);}else await Failure(response,status);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenWrongMethodPurpose_WhenPostingRefreshOrPuttingRecover_ThenReturn400WithoutMutation()
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var state=await Setup(old);Authorize(RegistrationApiFixture.Token(state.Actor));var refresh=Bytes(Replacement(old,next));var recover=Bytes(Replacement(old,next) with { Operation="recover" });var rc=await Challenge(refresh);var cc=await Challenge(recover,"recover");var before=await Snapshot(state);
        using(var response=await Recover(refresh,rc,old.Sign(rc)))await Failure(response,400);using(var response=await Send(recover,cc,old.SignRecovery(cc)))await Failure(response,400);Assert.Equal(before,await Snapshot(state));
    }
    private async Task<string> Unlock(ProtectionFixture key,long revision)
    {
        var raw=Bytes(new { expectedProtectionRevision=revision });var c=await Challenge(raw,"unlock-account");using var request=new HttpRequestMessage(HttpMethod.Post,"/api/vault/unlock") { Content=new ByteArrayContent(raw) };request.Content.Headers.ContentType=new("application/json");request.Headers.Add("X-Cerberus-Challenge-Id",c.ChallengeId.ToString());request.Headers.Add("X-Cerberus-Proof",key.Sign(c));
        using var response=await Gateway.Client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);return(await response.Content.ReadFromJsonAsync<DataOutput<VaultUnlockOutput>>())!.Data!.VaultAccess;
    }
    private async Task<HttpResponseMessage> Recover(byte[] raw,VaultProofChallenge c,string proof)
    {using var request=new HttpRequestMessage(HttpMethod.Post,"/api/vault/recovery") { Content=new ByteArrayContent(raw) };request.Content.Headers.ContentType=new("application/json");request.Headers.Add("X-Cerberus-Challenge-Id",c.ChallengeId.ToString());request.Headers.Add("X-Cerberus-Proof",proof);return await Gateway.Client.SendAsync(request);}
    private string? currentAccess;
    private sealed record State(Guid Actor,Guid Id,byte[] Envelope,string Access);
    private async Task<State> Setup(ProtectionFixture old,bool initialize=true)
    {
        var actor=Guid.NewGuid();var id=Guid.NewGuid();fixture.Users[Guid.NewGuid().ToString("N")+"@example.test"]=new(actor,"fixture-password");var envelope=Bytes(new EncryptedEnvelope("cerberus-content-v1",1,ProtectionFixture.Encode(new byte[32]),ProtectionFixture.Encode(new byte[12]),"AQID",ProtectionFixture.Encode(new byte[16])));
        await using var db=fixture.Context();var a=new Account { PublicId=id,HeimdallPublicId=actor,DetailsEnvelope=envelope,Revision=3 };db.Accounts.Add(a);await db.SaveChangesAsync();if(initialize){db.VaultProtections.Add(new VaultProtection { AccountId=a.Id,Material=Bytes(old.Material) });await db.SaveChangesAsync();}var access=ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32));OpaqueAccessHandle.TryHash(access,out var verifier);db.VaultAccessSessions.Add(new VaultAccessSession { AccountId=a.Id,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddSeconds(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1 });await db.SaveChangesAsync();currentAccess=access;return new(actor,id,envelope,access);
    }
    private static RecoveryReplacement Replacement(ProtectionFixture old,ProtectionFixture next,long epoch=2,long generation=2,long revision=1)
    {var m=old.Rewrap(epoch);return new("refresh-recovery",Guid.NewGuid(),revision,m.PasswordWrapper,m.RecoveryWrapper with { Generation=generation,ProofKeyFingerprint=next.Material.RecoveryVerifier.Fingerprint() },next.Material.RecoveryVerifier);}
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    private async Task<VaultProofChallenge> Challenge(byte[] raw,string operation="refresh-recovery")
    {using var response=await Gateway.Client.PostAsJsonAsync("/api/vault/challenges",new { operation,requestHash=ProtocolBinary.Encode(SHA256.HashData(raw)) });Assert.Equal(HttpStatusCode.Created,response.StatusCode);return(await response.Content.ReadFromJsonAsync<DataOutput<VaultChallengeOutput>>())!.Data!.Challenge;}
    private async Task<HttpResponseMessage> Send(byte[] raw,VaultProofChallenge c,string proof,string? access=null)
    {using var request=new HttpRequestMessage(HttpMethod.Put,"/api/vault/recovery") { Content=new ByteArrayContent(raw) };request.Content.Headers.ContentType=new("application/json");request.Headers.Add("X-Cerberus-Challenge-Id",c.ChallengeId.ToString());request.Headers.Add("X-Cerberus-Proof",proof);request.Headers.Add("X-Cerberus-Vault-Access",access??currentAccess!);return await Gateway.Client.SendAsync(request);}
    private async Task<string> Snapshot(State s)
    {await using var db=fixture.Context();var a=await db.Accounts.AsNoTracking().SingleAsync(x=>x.PublicId==s.Id);return JsonSerializer.Serialize(new { Account=a,Protection=await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==a.Id),Challenges=await db.VaultUnlockChallenges.AsNoTracking().Where(x=>x.AccountId==a.Id).OrderBy(x=>x.Id).ToListAsync(),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==a.Id).OrderBy(x=>x.Id).ToListAsync(),Outcomes=await db.VaultRecoveryOperations.AsNoTracking().Where(x=>x.AccountId==a.Id).OrderBy(x=>x.Id).ToListAsync() });}
    private static async Task Failure(HttpResponseMessage response,int status)
    {Assert.Equal((HttpStatusCode)status,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.False(doc.RootElement.GetProperty("success").GetBoolean());if(doc.RootElement.TryGetProperty("data",out var data))Assert.Equal(JsonValueKind.Null,data.ValueKind);}
}
