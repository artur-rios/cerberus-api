using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Configuration.Enums;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class OfflineAuthorizationHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenCurrentOwnerAndProfile_WhenRenewingOfflineAuthorization_ThenReturnSignedScopedPolicy()
    {
        var s = await Setup();
        Authorize(RegistrationApiFixture.Token(s.Identity));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/offline-authorizations")
        { Content = JsonContent.Create(new { profileId = s.Profile, expectedProfileRevision = 3 }) };
        request.Headers.Add("X-Cerberus-Vault-Access", s.Handle);
        using var response = await Gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(s.Account, data.GetProperty("accountId").GetGuid());
        Assert.Equal(s.Profile, data.GetProperty("profileId").GetGuid());
        Assert.Equal(2, data.GetProperty("keyEpoch").GetInt64());
        Assert.Equal(3, data.GetProperty("protectionRevision").GetInt64());
        Assert.Equal(1, data.GetProperty("policyRevision").GetInt64());
        Assert.Equal(1, data.GetProperty("revocationGeneration").GetInt64());
        var parts = data.GetProperty("lease").GetString()!.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.True(ProtocolBinary.TryDecode(parts[2], 64, out var signature));
        Assert.True(RegistrationApiFixture.LeasePublicKey.Verify(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature));
        Assert.True(ProtocolBinary.TryDecode(parts[1], null, out var payload));
        using var claims = JsonDocument.Parse(payload);
        Assert.Equal("profile", claims.RootElement.GetProperty("scopeKind").GetString());
        Assert.Equal(s.Profile, claims.RootElement.GetProperty("scopeId").GetGuid());
        Assert.Equal(86400, claims.RootElement.GetProperty("exp").GetInt64() - claims.RootElement.GetProperty("iat").GetInt64());
        await using var db = fixture.Context();
        Assert.Equal(7, await db.Accounts.Where(x => x.PublicId == s.Account).Select(x => x.Revision).SingleAsync());
        Assert.Equal(3, await db.Profiles.Where(x => x.PublicId == s.Profile).Select(x => x.Revision).SingleAsync());
    }

    [FunctionalTheory]
    [InlineData("selected", HttpStatusCode.OK)][InlineData("disabled", HttpStatusCode.OK)]
    [InlineData("missing", HttpStatusCode.NotFound)][InlineData("closing", HttpStatusCode.NotFound)]
    [InlineData("profileDeleted", HttpStatusCode.NotFound)][InlineData("otherSelection", HttpStatusCode.NotFound)]
    [InlineData("expired", HttpStatusCode.Forbidden)][InlineData("revoked", HttpStatusCode.Forbidden)]
    [InlineData("policy", HttpStatusCode.Forbidden)][InlineData("generation", HttpStatusCode.Forbidden)]
    [InlineData("expected", HttpStatusCode.Conflict)][InlineData("invalidEpoch", HttpStatusCode.ServiceUnavailable)]
    [InlineData("identityDeleted", HttpStatusCode.Unauthorized)][InlineData("identityUnavailable", HttpStatusCode.ServiceUnavailable)]
    public async Task GivenCurrentPolicyOrAuthority_WhenRenewing_ThenReturnExactScopedOutcome(string state, HttpStatusCode status)
    {
        var s = await Setup(state);
        Authorize(RegistrationApiFixture.Token(s.Identity));
        using var request = Request(s, "{\"profileId\":\"" + (state == "missing" ? Guid.NewGuid() : s.Profile) + "\",\"expectedProfileRevision\":" + (state == "expected" ? "2" : "3") + "}");
        using var response = await Gateway.Client.SendAsync(request);
        Assert.Equal(status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (status == HttpStatusCode.OK)
        {
            var data = json.RootElement.GetProperty("data");
            Assert.Equal(state != "disabled", data.GetProperty("renewalEnabled").GetBoolean());
            Assert.Equal(state == "disabled", data.GetProperty("expiresAt").ValueKind == JsonValueKind.Null);
            Assert.Equal(s.Profile, data.GetProperty("profileId").GetGuid());
            var parts = data.GetProperty("lease").GetString()!.Split('.');
            Assert.True(ProtocolBinary.TryDecode(parts[1], null, out var payload));
            using var claims = JsonDocument.Parse(payload);
            Assert.Equal(state != "disabled", claims.RootElement.TryGetProperty("exp", out _));
        }
        else Assert.True(!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null);
        await Unchanged(s);
    }

    [FunctionalTheory]
    [InlineData("{}")][InlineData("{\"profileId\":\"{profile}\"}")]
    [InlineData("{\"expectedProfileRevision\":3}")]
    [InlineData("{\"profileId\":\"invalid\",\"expectedProfileRevision\":3}")]
    [InlineData("{\"profileId\":\"00000000-0000-0000-0000-000000000000\",\"expectedProfileRevision\":3}")]
    [InlineData("{\"profileId\":\"{profile}\",\"expectedProfileRevision\":0}")]
    [InlineData("{\"profileId\":\"{profile}\",\"expectedProfileRevision\":9007199254740992}")]
    [InlineData("{\"profileId\":\"{profile}\",\"expectedProfileRevision\":3.5}")]
    [InlineData("{\"profileId\":\"{profile}\",\"expectedProfileRevision\":\"3\"}")]
    [InlineData("{\"profileId\":\"{profile}\",\"expectedProfileRevision\":3,\"accountId\":\"{profile}\"}")]
    [InlineData("{\"profileId\":\"{profile}\",\"expectedProfileRevision\":3,\"expectedProfileRevision\":3}")]
    [InlineData("{\"ProfileId\":\"{profile}\",\"expectedProfileRevision\":3}")]
    public async Task GivenInvalidStrictBody_WhenRenewing_ThenRejectWithoutLeaseOrMutation(string body)
    {
        var s = await Setup(); Authorize(RegistrationApiFixture.Token(s.Identity));
        using var request = Request(s, body.Replace("{profile}", s.Profile.ToString("D")));
        using var response = await Gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        await Unchanged(s);
    }

    [FunctionalTheory]
    [InlineData("noIdentity", HttpStatusCode.Unauthorized)][InlineData("noHandle", HttpStatusCode.Unauthorized)]
    [InlineData("handle", HttpStatusCode.BadRequest)][InlineData("duplicate", HttpStatusCode.BadRequest)]
    [InlineData("query", HttpStatusCode.BadRequest)][InlineData("compression", HttpStatusCode.BadRequest)]
    [InlineData("media", HttpStatusCode.BadRequest)][InlineData("textJson", HttpStatusCode.BadRequest)]
    [InlineData("vendorJson", HttpStatusCode.BadRequest)][InlineData("charset", HttpStatusCode.BadRequest)]
    [InlineData("parameter", HttpStatusCode.BadRequest)][InlineData("bom", HttpStatusCode.BadRequest)]
    [InlineData("empty", HttpStatusCode.BadRequest)]
    public async Task GivenInvalidTransport_WhenRenewing_ThenFailClosed(string invalid, HttpStatusCode status)
    {
        var s = await Setup(); if (invalid != "noIdentity") Authorize(RegistrationApiFixture.Token(s.Identity));
        using var request = Request(s, invalid == "empty" ? "" : (invalid == "bom" ? "\uFEFF" : "") + "{\"profileId\":\"" + s.Profile + "\",\"expectedProfileRevision\":3}");
        if (invalid == "noHandle") request.Headers.Remove("X-Cerberus-Vault-Access");
        if (invalid == "handle") { request.Headers.Remove("X-Cerberus-Vault-Access"); request.Headers.Add("X-Cerberus-Vault-Access", "invalid"); }
        if (invalid == "duplicate") request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access", s.Handle);
        if (invalid == "query") request.RequestUri = new("/api/offline-authorizations?profileId=" + s.Profile, UriKind.Relative);
        if (invalid == "compression") request.Content!.Headers.ContentEncoding.Add("gzip");
        if (invalid is "media" or "textJson" or "vendorJson") request.Content!.Headers.ContentType = new(invalid == "media" ? "text/plain" : invalid == "textJson" ? "text/json" : "application/cerberus+json");
        if (invalid == "charset") request.Content!.Headers.ContentType!.CharSet = "utf-16";
        if (invalid == "parameter") request.Content!.Headers.ContentType!.Parameters.Add(new("extra", "value"));
        using var response = await Gateway.Client.SendAsync(request);
        Assert.Equal(status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        await Unchanged(s);
    }

    [FunctionalFact]
    public async Task GivenOversizedBody_WhenRenewing_ThenReturnEmpty413()
    {
        var s = await Setup(); Authorize(RegistrationApiFixture.Token(s.Identity));
        using var request = Request(s, new string(' ', 65537));
        using var response = await Gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        await Unchanged(s);
    }
    private static HttpRequestMessage Request(SetupState s, string body)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/offline-authorizations") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        r.Headers.Add("X-Cerberus-Vault-Access", s.Handle); return r;
    }
    private async Task Unchanged(SetupState s)
    {
        await using var db = fixture.Context();
        Assert.Equal(7, await db.Accounts.Where(x => x.PublicId == s.Account).Select(x => x.Revision).SingleAsync());
        Assert.Equal(3, await db.Profiles.Where(x => x.PublicId == s.Profile).Select(x => x.Revision).SingleAsync());
        Assert.Equal(new byte[] { 11, 22 }, await db.Accounts.Where(x => x.PublicId == s.Account).Select(x => x.DetailsEnvelope).SingleAsync());
    }

    private async Task<SetupState> Setup(string state = "active")
    {
        var identity = Guid.NewGuid();
        fixture.Users[Guid.NewGuid() + "@example.test"] = new(identity, "fixture-password", Deleted: state == "identityDeleted", IdentityUnavailable: state == "identityUnavailable");
        var handle = ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32));
        Assert.True(OpaqueAccessHandle.TryHash(handle, out var verifier));
        await using var db = fixture.Context();
        var account = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = identity, Revision = 7, DetailsEnvelope = [11, 22],
            State = state == "closing" ? AccountState.ClosurePending : AccountState.Active,
            PolicyRevision = state == "policy" ? 2 : 1, RevocationGeneration = state == "generation" ? 2 : 1,
            RenewalEnabled = state != "disabled", RenewalInterval = state == "disabled" ? null : TimeSpan.FromHours(24) };
        db.Accounts.Add(account); await db.SaveChangesAsync();
        var profile = new Profile { AccountId = account.Id, PublicId = Guid.NewGuid(), Revision = 3,
            DeletedAt = state == "profileDeleted" ? DateTimeOffset.UtcNow : null,
            Envelope = JsonSerializer.SerializeToUtf8Bytes(new EncryptedEnvelope("cerberus-content-v1", state == "invalidEpoch" ? 0 : 2,
                ProtocolBinary.Encode(new byte[32]), ProtocolBinary.Encode(new byte[12]), "AQID", ProtocolBinary.Encode(new byte[16])), new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        db.Profiles.Add(profile); await db.SaveChangesAsync();
        db.VaultAccessSessions.Add(new VaultAccessSession { AccountId = account.Id, HandleVerifier = verifier,
            ProfileId = state == "selected" ? profile.Id : state == "otherSelection" ? long.MaxValue : null,
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = state == "disabled" ? null : state == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-1) : DateTimeOffset.UtcNow.AddHours(1),
            PolicyRevision = 1, RevocationGeneration = 1, Revoked = state == "revoked" });
        await db.SaveChangesAsync();
        return new(identity, account.PublicId, profile.PublicId, handle);
    }
    private sealed record SetupState(Guid Identity, Guid Account, Guid Profile, string Handle);
}
