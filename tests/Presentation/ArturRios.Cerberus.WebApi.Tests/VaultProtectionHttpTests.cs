using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class VaultProtectionHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string ChallengeHeader = "X-Cerberus-Challenge-Id";
    private const string ProofHeader = "X-Cerberus-Proof";
    private static readonly byte[] UnlockBody = Encoding.UTF8.GetBytes("{\"expectedProtectionRevision\":1}");

    [FunctionalFact]
    public async Task GivenCurrentOwner_WhenInitializingAndProvingUnlock_ThenIssueUsableHashedAccessWithoutVaultMutation()
    {
        using var client = new ProtectionFixture();var input = await Setup();Authorize(RegistrationApiFixture.Token(input.Actor));
        using var init = await Initialize(input.Id,client.Material);
        Assert.Equal(HttpStatusCode.Created,init.StatusCode);Assert.True(init.Headers.CacheControl?.NoStore);
        var confirmation = (await init.Content.ReadFromJsonAsync<DataOutput<VaultInitializationOutput>>())!.Data!;
        Assert.Equal(input.Id,confirmation.AccountId);Assert.Equal(1,confirmation.ProtectionRevision);
        var text = await init.Content.ReadAsStringAsync();Assert.DoesNotContain("material",text);Assert.DoesNotContain("vaultAccess",text);
        using var material = await Gateway.Client.GetAsync("/api/vault/protection");Assert.Equal(HttpStatusCode.OK,material.StatusCode);Assert.True(material.Headers.CacheControl?.NoStore);
        Assert.Equal(client.Material,(await material.Content.ReadFromJsonAsync<DataOutput<VaultProtectionOutput>>())!.Data!.Material);
        await AssertSessions(input.Actor,0);
        var c = await Challenge();await AssertSessions(input.Actor,0);
        using var unlock = await Unlock(c,client.Sign(c));Assert.Equal(HttpStatusCode.OK,unlock.StatusCode);Assert.True(unlock.Headers.CacheControl?.NoStore);
        var access = (await unlock.Content.ReadFromJsonAsync<DataOutput<VaultUnlockOutput>>())!.Data!;
        Assert.Equal(input.Id,access.AccountId);Assert.Equal(TimeSpan.FromHours(24),access.ExpiresAt-access.IssuedAt);
        Assert.True(OpaqueAccessHandle.TryHash(access.VaultAccess,out var verifier));
        await using (var db = fixture.Context())
        {
            var stored = await db.VaultAccessSessions.SingleAsync(x => x.HandleVerifier == verifier);
            Assert.NotEqual(access.VaultAccess,stored.HandleVerifier);Assert.Null(stored.ProfileId);
            Assert.Equal(1,stored.PolicyRevision);Assert.Equal(1,stored.RevocationGeneration);
        }
        using var read = new HttpRequestMessage(HttpMethod.Get,"/api/accounts/me");read.Headers.Add("X-Cerberus-Vault-Access",access.VaultAccess);
        using var account = await Gateway.Client.SendAsync(read);Assert.Equal(HttpStatusCode.OK,account.StatusCode);
        Assert.Equal(3,(await account.Content.ReadFromJsonAsync<DataOutput<AccountOutput>>())!.Data!.Revision);
        using var replay = await Unlock(c,client.Sign(c));await Failure(replay,HttpStatusCode.Unauthorized);
        Assert.Equal(input.Before,await Snapshot(input.Actor));await AssertSessions(input.Actor,1);
    }

    [FunctionalTheory]
    [InlineData("anonymous",HttpStatusCode.Unauthorized)][InlineData("scope",HttpStatusCode.Unauthorized)]
    [InlineData("deletedIdentity",HttpStatusCode.Unauthorized)][InlineData("identityUnavailable",HttpStatusCode.ServiceUnavailable)]
    [InlineData("missing",HttpStatusCode.NotFound)][InlineData("closing",HttpStatusCode.NotFound)]
    [InlineData("erased",HttpStatusCode.NotFound)][InlineData("foreignAccount",HttpStatusCode.NotFound)]
    [InlineData("stale",HttpStatusCode.Conflict)]
    public async Task GivenInvalidOwnerOrState_WhenInitializing_ThenNeverPersistProtection(string state,HttpStatusCode status)
    {
        using var client = new ProtectionFixture();var input = await Setup(state);
        if (state != "anonymous") Authorize(RegistrationApiFixture.Token(input.Actor,state == "scope" ? Guid.NewGuid() : null));
        using var response = await Initialize(state == "foreignAccount" ? (await Setup()).Id : input.Id,client.Material,state == "stale" ? 2 : 3);
        await Failure(response,status);Assert.Equal(input.Before,await Snapshot(input.Actor));await AssertSessions(input.Actor,0);
        await using var db = fixture.Context();Assert.False(await db.VaultProtections.AnyAsync(x => db.Accounts.Any(a => a.Id == x.AccountId && a.HeimdallPublicId == input.Actor)));
    }

    [FunctionalTheory]
    [InlineData("actor")][InlineData("password")][InlineData("privateJwk")][InlineData("duplicate")][InlineData("case")]
    [InlineData("stringRevision")][InlineData("decimalRevision")][InlineData("kdf")][InlineData("fingerprint")][InlineData("offCurve")]
    [InlineData("reusedKey")][InlineData("epoch")][InlineData("query")]
    public async Task GivenMalformedOrSecretBearingInitialization_WhenPosting_ThenReturn400WithoutWrites(string invalid)
    {
        using var client = new ProtectionFixture();var input = await Setup();Authorize(RegistrationApiFixture.Token(input.Actor));
        var m = client.Material;
        if (invalid == "kdf") m = m with { PasswordWrapper = m.PasswordWrapper with { Kdf = m.PasswordWrapper.Kdf with { MemoryKiB = 1 } } };
        if (invalid == "fingerprint") m = m with { RecoveryWrapper = m.RecoveryWrapper with { ProofKeyFingerprint = ProtocolBinary.Encode(new byte[32]) } };
        if (invalid == "offCurve") m = m with { AuthorKey = m.AuthorKey with { X = ProtocolBinary.Encode(new byte[32]),Y = ProtocolBinary.Encode(new byte[32]) } };
        if (invalid == "reusedKey") m = m with { AuthorKey = m.RecipientKey };
        if (invalid == "epoch") m = m with { PasswordWrapper = m.PasswordWrapper with { KeyEpoch = 2 },RecoveryWrapper = m.RecoveryWrapper with { KeyEpoch = 2 } };
        var body = JsonSerializer.Serialize(new { accountId = input.Id,expectedAccountRevision = 3,material = m },ProtectionFixture.Json);
        body = invalid switch
        {
            "actor" => body.Insert(1,"\"actor\":\""+Guid.NewGuid()+"\","),
            "password" => body.Insert(1,"\"vaultPassword\":\"secret\","),
            "privateJwk" => body.Replace("\"crv\":","\"d\":\"private\",\"crv\":"),
            "duplicate" => body.Insert(1,"\"expectedAccountRevision\":3,"),
            "case" => body.Replace("\"accountId\":","\"AccountId\":"),
            "stringRevision" => body.Replace("\"expectedAccountRevision\":3","\"expectedAccountRevision\":\"3\""),
            "decimalRevision" => body.Replace("\"expectedAccountRevision\":3","\"expectedAccountRevision\":3.0"), _ => body
        };
        using var response = await Gateway.Client.PostAsync("/api/vault/protection"+(invalid == "query" ? "?owner=untrusted" : ""),new StringContent(body,Encoding.UTF8,"application/json"));
        await Failure(response,HttpStatusCode.BadRequest);Assert.Equal(input.Before,await Snapshot(input.Actor));await AssertSessions(input.Actor,0);
        await using var db = fixture.Context();Assert.False(await db.VaultProtections.AnyAsync(x => db.Accounts.Any(a => a.Id == x.AccountId && a.HeimdallPublicId == input.Actor)));
    }

    [FunctionalFact]
    public async Task GivenConcurrentBootstrapRequests_WhenInitializing_ThenOnlyOneWinsAndRetryPreservesIt()
    {
        using var a = new ProtectionFixture();using var b = new ProtectionFixture();var input = await Setup();Authorize(RegistrationApiFixture.Token(input.Actor));
        var results = await Task.WhenAll(Initialize(input.Id,a.Material),Initialize(input.Id,b.Material));using var one = results[0];using var two = results[1];
        Assert.Single(results,x => x.StatusCode == HttpStatusCode.Created);Assert.Single(results,x => x.StatusCode == HttpStatusCode.Conflict);
        var winner = one.StatusCode == HttpStatusCode.Created ? a.Material : b.Material;
        using var retry = await Initialize(input.Id,a.Material);await Failure(retry,HttpStatusCode.Conflict);
        using var read = await Gateway.Client.GetAsync("/api/vault/protection");Assert.Equal(winner,(await read.Content.ReadFromJsonAsync<DataOutput<VaultProtectionOutput>>())!.Data!.Material);
        Assert.Equal(input.Before,await Snapshot(input.Actor));await AssertSessions(input.Actor,0);
    }

    [FunctionalTheory]
    [InlineData("old")][InlineData("future")][InlineData("missing")]
    public async Task GivenNoFreshSignedIssuedAt_WhenIssuingChallenge_ThenRequireReauthentication(string freshness)
    {
        using var client = new ProtectionFixture();var input = await Setup();Authorize(RegistrationApiFixture.Token(input.Actor));
        using var init = await Initialize(input.Id,client.Material);Assert.Equal(HttpStatusCode.Created,init.StatusCode);
        Authorize(TokenWithIssuedAt(input.Actor,freshness));
        using var response = await Gateway.Client.PostAsJsonAsync("/api/vault/challenges",new { operation = "unlock-account",requestHash = ProtocolBinary.Encode(SHA256.HashData(UnlockBody)) });
        await Failure(response,HttpStatusCode.Unauthorized);await AssertSessions(input.Actor,0);
        await using var db = fixture.Context();Assert.False(await db.VaultUnlockChallenges.AnyAsync(x => db.Accounts.Any(a => a.Id == x.AccountId && a.HeimdallPublicId == input.Actor)));
    }

    [FunctionalTheory]
    [InlineData("body",HttpStatusCode.Unauthorized)][InlineData("signature",HttpStatusCode.Unauthorized)]
    [InlineData("foreignActor",HttpStatusCode.Unauthorized)][InlineData("missingHeader",HttpStatusCode.Unauthorized)]
    [InlineData("duplicateHeader",HttpStatusCode.BadRequest)][InlineData("malformedHeader",HttpStatusCode.BadRequest)]
    [InlineData("unknownBody",HttpStatusCode.BadRequest)][InlineData("duplicateBody",HttpStatusCode.BadRequest)]
    [InlineData("numericString",HttpStatusCode.BadRequest)][InlineData("query",HttpStatusCode.BadRequest)]
    [InlineData("compressed",HttpStatusCode.BadRequest)][InlineData("bom",HttpStatusCode.BadRequest)]
    [InlineData("invalidUtf8",HttpStatusCode.BadRequest)][InlineData("stale",HttpStatusCode.Conflict)]
    public async Task GivenInvalidProofOrRawBody_WhenUnlocking_ThenIssueNoSession(string invalid,HttpStatusCode status)
    {
        using var client = new ProtectionFixture();var input = await Setup();Authorize(RegistrationApiFixture.Token(input.Actor));
        using var init = await Initialize(input.Id,client.Material);Assert.Equal(HttpStatusCode.Created,init.StatusCode);
        var c = await Challenge();
        if (invalid == "foreignActor")
        {
            var other = await Setup();Authorize(RegistrationApiFixture.Token(other.Actor));
            using var otherInit = await Initialize(other.Id,client.Material);Assert.Equal(HttpStatusCode.Created,otherInit.StatusCode);
        }
        byte[] body = invalid switch
        {
            "body" => Encoding.UTF8.GetBytes("{ \"expectedProtectionRevision\":1}"),
            "unknownBody" => Encoding.UTF8.GetBytes("{\"expectedProtectionRevision\":1,\"actor\":\"untrusted\"}"),
            "duplicateBody" => Encoding.UTF8.GetBytes("{\"expectedProtectionRevision\":1,\"expectedProtectionRevision\":1}"),
            "numericString" => Encoding.UTF8.GetBytes("{\"expectedProtectionRevision\":\"1\"}"),
            "stale" => Encoding.UTF8.GetBytes("{\"expectedProtectionRevision\":2}"),
            "bom" => new byte[] { 239,187,191 }.Concat(UnlockBody).ToArray(),
            "invalidUtf8" => new byte[] { 123,34,120,34,58,34,255,34,125 }, _ => UnlockBody
        };
        using var request = new HttpRequestMessage(HttpMethod.Post,"/api/vault/unlock"+(invalid == "query" ? "?owner=untrusted" : ""))
            { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        if (invalid == "compressed") request.Content.Headers.ContentEncoding.Add("gzip");
        if (invalid != "missingHeader") request.Headers.TryAddWithoutValidation(ChallengeHeader,invalid == "duplicateHeader" ? new[] { c.ChallengeId.ToString(),c.ChallengeId.ToString() } : new[] { invalid == "malformedHeader" ? "BAD" : c.ChallengeId.ToString() });
        request.Headers.Add(ProofHeader,invalid == "signature" ? ProtocolBinary.Encode(new byte[64]) : client.Sign(c));
        using var response = await Gateway.Client.SendAsync(request);await Failure(response,status);await AssertSessions(input.Actor,0);
        Assert.Equal(input.Before,await Snapshot(input.Actor));
    }

    [FunctionalFact]
    public async Task GivenTwoSameChallengeProofs_WhenUnlockingConcurrently_ThenOneHandleCommits()
    {
        using var client = new ProtectionFixture();var input = await Setup();Authorize(RegistrationApiFixture.Token(input.Actor));
        using var init = await Initialize(input.Id,client.Material);Assert.Equal(HttpStatusCode.Created,init.StatusCode);
        var c = await Challenge();var proof = client.Sign(c);var responses = await Task.WhenAll(Unlock(c,proof),Unlock(c,proof));
        using var one = responses[0];using var two = responses[1];
        Assert.Single(responses,x => x.StatusCode == HttpStatusCode.OK);Assert.Single(responses,x => x.StatusCode == HttpStatusCode.Unauthorized);
        await AssertSessions(input.Actor,1);Assert.Equal(input.Before,await Snapshot(input.Actor));
    }

    [FunctionalTheory]
    [InlineData("readMissing",HttpStatusCode.NotFound)][InlineData("challengeMissing",HttpStatusCode.NotFound)]
    [InlineData("readQuery",HttpStatusCode.BadRequest)][InlineData("readBody",HttpStatusCode.BadRequest)]
    [InlineData("challengeQuery",HttpStatusCode.BadRequest)][InlineData("invalidHash",HttpStatusCode.BadRequest)]
    [InlineData("unknownOperation",HttpStatusCode.BadRequest)][InlineData("unknownField",HttpStatusCode.BadRequest)]
    [InlineData("readClosing",HttpStatusCode.NotFound)][InlineData("challengeClosing",HttpStatusCode.NotFound)]
    public async Task GivenInvalidMaterialReadOrChallenge_WhenRequesting_ThenReturnStableFailureWithoutSessions(string invalid,HttpStatusCode status)
    {
        using var client = new ProtectionFixture();var input = await Setup();Authorize(RegistrationApiFixture.Token(input.Actor));
        if (!invalid.EndsWith("Missing",StringComparison.Ordinal))
        { using var init = await Initialize(input.Id,client.Material);Assert.Equal(HttpStatusCode.Created,init.StatusCode); }
        if (invalid.EndsWith("Closing",StringComparison.Ordinal))
        { await using var db = fixture.Context();var account = await db.Accounts.SingleAsync(x => x.PublicId == input.Id);account.State = AccountState.ClosurePending;await db.SaveChangesAsync(); }
        var before = await Snapshot(input.Actor);
        HttpResponseMessage response;
        if (invalid.StartsWith("read",StringComparison.Ordinal))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,"/api/vault/protection"+(invalid == "readQuery" ? "?owner=untrusted" : ""));
            if (invalid == "readBody") request.Content = JsonContent.Create(new { owner = "untrusted" });
            response = await Gateway.Client.SendAsync(request);
        }
        else
        {
            var body = JsonSerializer.Serialize(new { operation = invalid == "unknownOperation" ? "recover" : "unlock-account",
                requestHash = invalid == "invalidHash" ? "bad" : ProtocolBinary.Encode(SHA256.HashData(UnlockBody)) });
            if (invalid == "unknownField") body = body.Insert(1,"\"owner\":\"untrusted\",");
            response = await Gateway.Client.PostAsync("/api/vault/challenges"+(invalid == "challengeQuery" ? "?owner=untrusted" : ""),new StringContent(body,Encoding.UTF8,"application/json"));
        }
        using (response) await Failure(response,status);
        Assert.Equal(before,await Snapshot(input.Actor));await AssertSessions(input.Actor,0);
    }

    [FunctionalFact]
    public async Task GivenWhitespaceBodySignedByClient_WhenUnlocking_ThenVerifyOriginalBytesWithoutReserialization()
    {
        using var client = new ProtectionFixture();var input = await Setup();Authorize(RegistrationApiFixture.Token(input.Actor));
        using var init = await Initialize(input.Id,client.Material);Assert.Equal(HttpStatusCode.Created,init.StatusCode);
        var raw = Encoding.UTF8.GetBytes("{\n  \"expectedProtectionRevision\": 1\n}");
        var challenge = await Challenge(raw);using var response = await Unlock(challenge,client.Sign(challenge),raw);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);
        await AssertSessions(input.Actor,1);Assert.Equal(input.Before,await Snapshot(input.Actor));
    }

    [FunctionalFact]
    public async Task GivenUnavailablePersistence_WhenInitializing_ThenReturn503AndWriteNothing()
    {
        using var client = new ProtectionFixture();var input = await Setup();Authorize(RegistrationApiFixture.Token(input.Actor));
        await using var db = fixture.Context();await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.account RENAME TO fixture_vault_unavailable_account");
        try { using var response = await Initialize(input.Id,client.Material);await Failure(response,HttpStatusCode.ServiceUnavailable); }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_vault_unavailable_account RENAME TO account"); }
        Assert.Equal(input.Before,await Snapshot(input.Actor));await AssertSessions(input.Actor,0);
    }

    private async Task<(Guid Actor,Guid Id,string Before)> Setup(string state = "active")
    {
        var actor = Guid.NewGuid();var id = Guid.NewGuid();fixture.Users[Guid.NewGuid().ToString("N")+"@example.test"] = new(actor,"fixture-password",Deleted:state == "deletedIdentity",IdentityUnavailable:state == "identityUnavailable");
        if (state != "missing")
        {
            await using var db = fixture.Context();var account = new Account { PublicId = id,HeimdallPublicId = actor,Revision = 3,
                DetailsEnvelope = JsonSerializer.SerializeToUtf8Bytes(new EncryptedEnvelope("cerberus-content-v1",1,ProtocolBinary.Encode(new byte[32]),ProtocolBinary.Encode(new byte[12]),"AQID",ProtocolBinary.Encode(new byte[16])),ProtectionFixture.Json),
                State = state == "closing" ? AccountState.ClosurePending : AccountState.Active };
            db.Accounts.Add(account);await db.SaveChangesAsync();
            if (state == "erased") { db.TerminalErasures.Add(new TerminalErasure { ResourceId = id,ResourceKind = "account",DeletedAt = DateTimeOffset.UtcNow });await db.SaveChangesAsync(); }
        }
        return (actor,id,await Snapshot(actor));
    }
    private async Task<string> Snapshot(Guid actor)
    { await using var db = fixture.Context();return JsonSerializer.Serialize(await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.HeimdallPublicId == actor)); }
    private async Task AssertSessions(Guid actor,int count)
    { await using var db = fixture.Context();Assert.Equal(count,await db.VaultAccessSessions.CountAsync(x => db.Accounts.Any(a => a.Id == x.AccountId && a.HeimdallPublicId == actor))); }
    private Task<HttpResponseMessage> Initialize(Guid accountId,ProtectionMaterial material,long revision = 3) =>
        Gateway.Client.PostAsJsonAsync("/api/vault/protection",new { accountId,expectedAccountRevision = revision,material });
    private async Task<VaultProofChallenge> Challenge(byte[]? raw = null)
    {
        using var response = await Gateway.Client.PostAsJsonAsync("/api/vault/challenges",new { operation = "unlock-account",requestHash = ProtocolBinary.Encode(SHA256.HashData(raw ?? UnlockBody)) });
        Assert.Equal(HttpStatusCode.Created,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<DataOutput<VaultChallengeOutput>>())!.Data!.Challenge;
    }
    private async Task<HttpResponseMessage> Unlock(VaultProofChallenge c,string proof,byte[]? raw = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,"/api/vault/unlock") { Content = new ByteArrayContent(raw ?? UnlockBody) };
        request.Content.Headers.ContentType = new("application/json");request.Headers.Add(ChallengeHeader,c.ChallengeId.ToString());request.Headers.Add(ProofHeader,proof);
        return await Gateway.Client.SendAsync(request);
    }
    private static async Task Failure(HttpResponseMessage response,HttpStatusCode status)
    {
        Assert.Equal(status,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);
        using var output = JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.False(output.RootElement.GetProperty("success").GetBoolean());
        if (output.RootElement.TryGetProperty("data",out var data)) Assert.Equal(JsonValueKind.Null,data.ValueKind);
    }
    private static string TokenWithIssuedAt(Guid actor,string freshness)
    {
        var claims = new List<Claim> { new("id",actor.ToString()),new("roleId","3"),new("scopeId",RegistrationApiFixture.Scope.ToString()) };
        if (freshness != "missing") claims.Add(new("iat",(DateTimeOffset.UtcNow.ToUnixTimeSeconds()+(freshness == "old" ? -120 : 120)).ToString(),ClaimValueTypes.Integer64));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("fixture-issuer","fixture-audience",claims,
            DateTime.UtcNow.AddMinutes(-5),DateTime.UtcNow.AddMinutes(5),new SigningCredentials(new SymmetricSecurityKey(Encoding.ASCII.GetBytes("fixture-only-signing-key-32-characters")),SecurityAlgorithms.HmacSha256)));
    }
}
