using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Configuration.Enums;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class OfflinePolicyUpdateHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalTheory]
    [InlineData("1 month")]
    [InlineData("20000000 days")]
    public async Task GivenUndecodablePersistedInterval_WhenSettingPolicy_ThenReturn503WithoutMutation(string interval)
    {
        var input = await Setup();
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.account SET renewal_interval=CAST({interval} AS interval) WHERE public_id={input.Account}");
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var request = Request(input.Handle, "{\"expectedPolicyRevision\":1,\"renewalEnabled\":true,\"renewalInterval\":\"00:00:01\"}");
        using var response = await Gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("persistence_unavailable", await response.Content.ReadAsStringAsync());
        var saved = await db.Accounts.AsNoTracking().Where(x => x.PublicId == input.Account)
            .Select(x => new { x.Revision, x.PolicyRevision, x.RevocationGeneration, x.DetailsEnvelope }).SingleAsync();
        Assert.Equal(7, saved.Revision);
        Assert.Equal(1, saved.PolicyRevision);
        Assert.Equal(1, saved.RevocationGeneration);
        Assert.Equal(new byte[] { 11, 22 }, saved.DetailsEnvelope);
    }

    [FunctionalFact]
    public async Task GivenCurrentOwner_WhenUpdatingOfflinePolicy_ThenVersionPolicyAndPreserveContent()
    {
        var identity = Guid.NewGuid();
        fixture.Users[Guid.NewGuid() + "@example.test"] = new(identity, "fixture-password");
        var handle = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.True(OpaqueAccessHandle.TryHash(handle, out var verifier));
        await using var db = fixture.Context();
        var account = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = identity, DetailsEnvelope = [11, 22], Revision = 7 };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        db.VaultAccessSessions.Add(new VaultAccessSession { AccountId = account.Id, HandleVerifier = verifier,
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            PolicyRevision = 1, RevocationGeneration = 1 });
        await db.SaveChangesAsync();
        Authorize(RegistrationApiFixture.Token(identity));
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/accounts/me/offline-policy")
        {
            Content = JsonContent.Create(new { expectedPolicyRevision = 1, renewalEnabled = true, renewalInterval = "00:00:01" })
        };
        request.Headers.Add("X-Cerberus-Vault-Access", handle);
        using var response = await Gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var raw = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(raw);
        var data = json.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("renewalEnabled").GetBoolean());
        Assert.Equal("00:00:01", data.GetProperty("renewalInterval").GetString());
        Assert.Equal(2, data.GetProperty("policyRevision").GetInt64());
        Assert.Equal(1, data.GetProperty("revocationGeneration").GetInt64());
        Assert.Equal(4, data.EnumerateObject().Count());
        Assert.DoesNotContain(handle, raw);
        Assert.DoesNotContain("ciphertext", raw);
        await using var after = fixture.Context();
        var saved = await after.Accounts.SingleAsync(x => x.Id == account.Id);
        Assert.Equal(2, saved.PolicyRevision);
        Assert.Equal(TimeSpan.FromSeconds(1), saved.RenewalInterval);
        Assert.Equal(7, saved.Revision);
        Assert.Equal(new byte[] { 11, 22 }, saved.DetailsEnvelope);
    }
    [FunctionalTheory]
    [InlineData("disabled", HttpStatusCode.OK)]
    [InlineData("selected", HttpStatusCode.OK)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("closing", HttpStatusCode.NotFound)]
    [InlineData("erased", HttpStatusCode.NotFound)]
    [InlineData("revoked", HttpStatusCode.Forbidden)]
    [InlineData("expired", HttpStatusCode.Forbidden)]
    [InlineData("stalePolicy", HttpStatusCode.Forbidden)]
    [InlineData("staleGeneration", HttpStatusCode.Forbidden)]
    [InlineData("badProfile", HttpStatusCode.Forbidden)]
    [InlineData("staleExpected", HttpStatusCode.Conflict)]
    [InlineData("exhausted", HttpStatusCode.Conflict)]
    [InlineData("corruptPolicy", HttpStatusCode.ServiceUnavailable)]
    public async Task GivenSavedPolicyAndAccess_WhenSetting_ThenEnforceCurrentAuthorityAndExpectedVersion(string state, HttpStatusCode status)
    {
        var input = await Setup(state);
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var request = Request(input.Handle, state == "disabled"
            ? "{\"expectedPolicyRevision\":1,\"renewalEnabled\":false,\"renewalInterval\":null}"
            : "{\"expectedPolicyRevision\":" + (state == "staleExpected" ? "2" : state == "exhausted" ? "9007199254740991" : "1") + ",\"renewalEnabled\":true,\"renewalInterval\":\"00:00:01\"}");
        using var response = await Gateway.Client.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (status == HttpStatusCode.OK)
        {
            var data = json.RootElement.GetProperty("data");
            Assert.Equal(state != "disabled", data.GetProperty("renewalEnabled").GetBoolean());
            Assert.Equal(state == "disabled" ? null : "00:00:01", data.GetProperty("renewalInterval").GetString());
            Assert.Equal(2, data.GetProperty("policyRevision").GetInt64());
            using var get = new HttpRequestMessage(HttpMethod.Get, "/api/accounts/me/offline-policy");
            get.Headers.Add("X-Cerberus-Vault-Access", input.Handle);
            using var stale = await Gateway.Client.SendAsync(get);
            Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        }
        else Assert.True(!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null);
        if (state != "missing")
        {
            await using var db = fixture.Context();
            var account = await db.Accounts.SingleAsync(x => x.PublicId == input.Account);
            Assert.Equal(status == HttpStatusCode.OK ? 2L : state == "exhausted" ? 9007199254740991L : state == "stalePolicy" ? 2L : 1L, account.PolicyRevision);
            Assert.Equal(7, account.Revision);
            Assert.Equal(new byte[] { 11, 22 }, account.DetailsEnvelope);
        }
    }

    [FunctionalTheory]
    [InlineData("{}")]
    [InlineData("{\"expectedPolicyRevision\":1,\"renewalEnabled\":true}")]
    [InlineData("{\"expectedPolicyRevision\":1,\"renewalInterval\":null}")]
    [InlineData("{\"renewalEnabled\":false,\"renewalInterval\":null}")]
    [InlineData("{\"expectedPolicyRevision\":1,\"renewalEnabled\":true,\"renewalInterval\":null}")]
    [InlineData("{\"expectedPolicyRevision\":1,\"renewalEnabled\":false,\"renewalInterval\":\"00:00:01\"}")]
    [InlineData("{\"expectedPolicyRevision\":1,\"renewalEnabled\":true,\"renewalInterval\":\"00:00:00\"}")]
    [InlineData("{\"expectedPolicyRevision\":1,\"renewalEnabled\":true,\"renewalInterval\":\"-00:00:01\"}")]
    [InlineData("{\"expectedPolicyRevision\":1,\"renewalEnabled\":true,\"renewalInterval\":\"00:00:00.5\"}")]
    [InlineData("{\"expectedPolicyRevision\":1,\"renewalEnabled\":true,\"renewalInterval\":1}")]
    [InlineData("{\"expectedPolicyRevision\":1,\"renewalEnabled\":true,\"renewalInterval\":\"invalid\"}")]
    [InlineData("{\"expectedPolicyRevision\":1,\"renewalEnabled\":true,\"renewalInterval\":\"10675199.02:48:05.4775807\"}")]
    [InlineData("{\"expectedPolicyRevision\":1,\"renewalEnabled\":true,\"renewalEnabled\":false,\"renewalInterval\":null}")]
    [InlineData("{\"ExpectedPolicyRevision\":1,\"renewalEnabled\":false,\"renewalInterval\":null}")]
    [InlineData("{\"expectedPolicyRevision\":1.5,\"renewalEnabled\":false,\"renewalInterval\":null}")]
    [InlineData("{\"expectedPolicyRevision\":\"1\",\"renewalEnabled\":false,\"renewalInterval\":null}")]
    [InlineData("{\"expectedPolicyRevision\":0,\"renewalEnabled\":false,\"renewalInterval\":null}")]
    [InlineData("{\"expectedPolicyRevision\":9007199254740992,\"renewalEnabled\":false,\"renewalInterval\":null}")]
    [InlineData("{\"expectedPolicyRevision\":1,\"renewalEnabled\":false,\"renewalInterval\":null,\"ownerId\":\"untrusted\"}")]
    public async Task GivenInvalidVisibleBody_WhenSetting_ThenRejectWithoutMutation(string body)
    {
        var input = await Setup();
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var request = Request(input.Handle, body);
        using var response = await Gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        await Unchanged(input.Account);
    }

    [FunctionalTheory]
    [InlineData("noIdentity", HttpStatusCode.Unauthorized)]
    [InlineData("noHandle", HttpStatusCode.Unauthorized)]
    [InlineData("handle", HttpStatusCode.BadRequest)]
    [InlineData("duplicate", HttpStatusCode.BadRequest)]
    [InlineData("query", HttpStatusCode.BadRequest)]
    [InlineData("compression", HttpStatusCode.BadRequest)]
    [InlineData("media", HttpStatusCode.BadRequest)]
    [InlineData("charset", HttpStatusCode.BadRequest)]
    [InlineData("parameter", HttpStatusCode.BadRequest)]
    [InlineData("bom", HttpStatusCode.BadRequest)]
    [InlineData("empty", HttpStatusCode.BadRequest)]
    public async Task GivenInvalidTransportOrContext_WhenSetting_ThenFailClosed(string invalid, HttpStatusCode status)
    {
        var input = await Setup();
        if (invalid != "noIdentity") Authorize(RegistrationApiFixture.Token(input.Identity));
        using var request = Request(invalid == "noHandle" ? null : invalid == "handle" ? "invalid" : input.Handle,
            invalid == "empty" ? "" : (invalid == "bom" ? "\uFEFF" : "") + "{\"expectedPolicyRevision\":1,\"renewalEnabled\":false,\"renewalInterval\":null}");
        if (invalid == "duplicate") request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access", input.Handle);
        if (invalid == "query") request.RequestUri = new("/api/accounts/me/offline-policy?ownerId=untrusted", UriKind.Relative);
        if (invalid == "compression") request.Content!.Headers.ContentEncoding.Add("gzip");
        if (invalid == "media") request.Content!.Headers.ContentType = new("text/plain");
        if (invalid == "charset") request.Content!.Headers.ContentType!.CharSet = "utf-16";
        if (invalid == "parameter") request.Content!.Headers.ContentType!.Parameters.Add(new("extra", "value"));
        using var response = await Gateway.Client.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        await Unchanged(input.Account);
    }

    [FunctionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenCurrentIdentityDeniedOrUnavailable_WhenSetting_ThenFailClosed(bool unavailable)
    {
        var input = await Setup();
        var pair = fixture.Users.Single(x => x.Value.Id == input.Identity);
        fixture.Users[pair.Key] = pair.Value with { Deleted = !unavailable, IdentityUnavailable = unavailable };
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var request = Request(input.Handle, "{\"expectedPolicyRevision\":1,\"renewalEnabled\":false,\"renewalInterval\":null}");
        using var response = await Gateway.Client.SendAsync(request);
        Assert.Equal(unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Unauthorized, response.StatusCode);
        await Unchanged(input.Account);
    }

    [FunctionalFact]
    public async Task GivenAnotherOwnersHandle_WhenSetting_ThenDenyAndPreserveBothPolicies()
    {
        var owner = await Setup(); var other = await Setup();
        Authorize(RegistrationApiFixture.Token(owner.Identity));
        using var request = Request(other.Handle, "{\"expectedPolicyRevision\":1,\"renewalEnabled\":false,\"renewalInterval\":null}");
        using var response = await Gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(other.Account.ToString(), await response.Content.ReadAsStringAsync());
        await Unchanged(owner.Account); await Unchanged(other.Account);
    }

    [FunctionalFact]
    public async Task GivenUnavailablePersistence_WhenSetting_ThenReturn503AndPreserveState()
    {
        var input = await Setup();
        Authorize(RegistrationApiFixture.Token(input.Identity));
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.vault_access_session RENAME TO fixture_policy_unavailable_session");
        try
        {
            using var request = Request(input.Handle, "{\"expectedPolicyRevision\":1,\"renewalEnabled\":false,\"renewalInterval\":null}");
            using var response = await Gateway.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_policy_unavailable_session RENAME TO vault_access_session"); }
        await Unchanged(input.Account);
    }

    private async Task<(Guid Identity, Guid Account, string Handle)> Setup(string state = "active")
    {
        var identity = Guid.NewGuid(); var id = Guid.NewGuid();
        fixture.Users[Guid.NewGuid() + "@example.test"] = new(identity, "fixture-password");
        var handle = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.True(OpaqueAccessHandle.TryHash(handle, out var verifier));
        if (state != "missing")
        {
            await using var db = fixture.Context();
            var account = new Account { PublicId = id, HeimdallPublicId = identity, DetailsEnvelope = [11, 22], Revision = 7,
                PolicyRevision = state == "exhausted" ? 9007199254740991 : state == "stalePolicy" ? 2 : 1,
                RenewalInterval = state == "corruptPolicy" ? TimeSpan.Zero : TimeSpan.FromHours(24), RevocationGeneration = state == "staleGeneration" ? 2 : 1,
                State = state == "closing" ? AccountState.ClosurePending : AccountState.Active };
            db.Accounts.Add(account); await db.SaveChangesAsync();
            long? selected = null;
            if (state == "selected")
            {
                var profile = new ArturRios.Cerberus.Domain.Profiles.Profile { AccountId = account.Id, PublicId = Guid.NewGuid() };
                db.Profiles.Add(profile); await db.SaveChangesAsync(); selected = profile.Id;
            }
            db.VaultAccessSessions.Add(new VaultAccessSession { AccountId = account.Id, HandleVerifier = verifier,
                IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = state == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddHours(1),
                PolicyRevision = state == "exhausted" ? 9007199254740991 : 1, RevocationGeneration = 1, Revoked = state == "revoked", ProfileId = state == "badProfile" ? long.MaxValue : selected });
            if (state == "erased") db.TerminalErasures.Add(new ArturRios.Cerberus.Domain.Operations.TerminalErasure { ResourceKind = "account", ResourceId = id, DeletedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        return (identity, id, handle);
    }
    private static HttpRequestMessage Request(string? handle, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/accounts/me/offline-policy")
        { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        if (handle is not null) request.Headers.Add("X-Cerberus-Vault-Access", handle);
        return request;
    }
    private async Task Unchanged(Guid id)
    {
        await using var db = fixture.Context();
        var account = await db.Accounts.SingleAsync(x => x.PublicId == id);
        Assert.Equal(1, account.PolicyRevision); Assert.Equal(7, account.Revision);
        Assert.True(account.RenewalEnabled); Assert.Equal(TimeSpan.FromHours(24), account.RenewalInterval);
        Assert.Equal(new byte[] { 11, 22 }, account.DetailsEnvelope);
    }

}
