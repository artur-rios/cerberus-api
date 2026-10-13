using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Query.Accounts;
using ArturRios.Configuration.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class OfflinePolicyHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Header = "X-Cerberus-Vault-Access";

    [FunctionalTheory]
    [InlineData("active", true, "1.00:00:00")]
    [InlineData("configured", true, "00:00:01")]
    [InlineData("disabled", false, null)]
    [InlineData("selected", true, "1.00:00:00")]
    public async Task GivenCurrentOwner_WhenReadingOfflinePolicy_ThenReturnOnlySavedPolicy(string state, bool enabled, string? interval)
    {
        var input = await Setup(state);
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var response = await Send(input.Handle);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var raw = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(raw);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(enabled, data.GetProperty("renewalEnabled").GetBoolean());
        Assert.Equal(interval, data.GetProperty("renewalInterval").GetString());
        Assert.Equal(1, data.GetProperty("policyRevision").GetInt64());
        Assert.Equal(1, data.GetProperty("revocationGeneration").GetInt64());
        Assert.Equal(4, data.EnumerateObject().Count());
        Assert.DoesNotContain("details", raw);
        Assert.DoesNotContain("ciphertext", raw);
        Assert.DoesNotContain(input.AccountId.ToString(), raw);
        Assert.DoesNotContain(input.Handle, raw);
        await using var db = fixture.Context();
        Assert.Equal(3, (await db.Accounts.SingleAsync(x => x.PublicId == input.AccountId)).Revision);
    }

    [FunctionalFact]
    public async Task GivenNoIdentity_WhenReading_ThenRequireAuthentication()
    {
        using var response = await Send(null);
        await Failure(response, HttpStatusCode.Unauthorized);
    }

    [FunctionalFact]
    public async Task GivenIdentityWithoutVaultAccess_WhenReading_ThenRequireSeparateAccess()
    {
        var input = await Setup();
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var response = await Send(null);
        await Failure(response, HttpStatusCode.Unauthorized);
    }

    [FunctionalFact]
    public async Task GivenOtherOwnersHandle_WhenReading_ThenDenyWithoutCrossAccountDisclosure()
    {
        var owner = await Setup();
        var other = await Setup();
        Authorize(RegistrationApiFixture.Token(owner.Identity));
        using var response = await Send(other.Handle);
        await Failure(response, HttpStatusCode.Forbidden);
        Assert.DoesNotContain(other.AccountId.ToString(), await response.Content.ReadAsStringAsync());
    }

    [FunctionalTheory]
    [InlineData("missing")]
    [InlineData("closing")]
    [InlineData("erased")]
    public async Task GivenAbsentOrHiddenAccount_WhenReading_ThenReturnNonrevealingNotFound(string state)
    {
        var input = await Setup(state);
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var response = await Send(input.Handle);
        await Failure(response, HttpStatusCode.NotFound);
    }

    [FunctionalTheory]
    [InlineData("profile")]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("stale")]
    [InlineData("staleGeneration")]
    [InlineData("zeroPolicy")]
    public async Task GivenInvalidSelectedAccess_WhenReading_ThenDenyWithoutCiphertext(string state)
    {
        var input = await Setup(state);
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var response = await Send(input.Handle);
        await Failure(response, HttpStatusCode.Forbidden);
    }

    [FunctionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenCurrentIdentityDeniedOrUnavailable_WhenReading_ThenFailClosed(bool unavailable)
    {
        var input = await Setup();
        var pair = fixture.Users.Single(x => x.Value.Id == input.Identity);
        fixture.Users[pair.Key] = pair.Value with { Deleted = !unavailable, IdentityUnavailable = unavailable };
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var response = await Send(input.Handle);
        await Failure(response, unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Unauthorized);
    }

    [FunctionalTheory]
    [InlineData("handle")]
    [InlineData("duplicate")]
    [InlineData("query")]
    [InlineData("body")]
    [InlineData("http2Body")]
    [InlineData("emptyHandle")]
    [InlineData("whitespaceHandle")]
    public async Task GivenUnexpectedVisibleInput_WhenReading_ThenRejectWithoutContent(string invalid)
    {
        var input = await Setup();
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/accounts/me/offline-policy" + (invalid == "query" ? "?ownerId=" + Guid.NewGuid() : ""));
        request.Headers.TryAddWithoutValidation(Header, invalid == "duplicate" ? new[] { input.Handle, input.Handle } : new[] { invalid == "handle" ? "bad" : invalid == "emptyHandle" ? "" : invalid == "whitespaceHandle" ? " " : input.Handle });
        if (invalid == "http2Body")
        {
            request.Version = HttpVersion.Version20;
            request.Content = new UnspecifiedLengthContent();
        }
        if (invalid == "body") request.Content = new StringContent("{\"ownerId\":\"untrusted\"}", Encoding.UTF8, "application/json");
        if (invalid == "emptyHandle")
        {
            // HttpClient/TestServer omits zero-value headers. Inject the actual
            // wire-equivalent empty value into the host so absence is not tested.
            using var host = new WebApplicationFactory<Program>();
            var raw = await host.Server.SendAsync(context =>
            {
                context.Request.Method = "GET";
                context.Request.Path = "/api/accounts/me/offline-policy";
                context.Request.Headers.Authorization = "Bearer " + RegistrationApiFixture.Token(input.Identity);
                context.Request.Headers[Header] = "";
            });
            Assert.Equal(400, raw.Response.StatusCode);
            Assert.Contains("no-store", raw.Response.Headers.CacheControl.ToString());
            var result = (await JsonSerializer.DeserializeAsync<DataOutput<JsonElement?>>(raw.Response.Body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
            Assert.False(result.Success);
            Assert.Null(result.Data);
        }
        else
        {
            using var response = await Gateway.Client.SendAsync(request);
            await Failure(response, HttpStatusCode.BadRequest);
        }
        await using var context = fixture.Context();
        Assert.Equal(3, (await context.Accounts.SingleAsync(x => x.PublicId == input.AccountId)).Revision);
    }

    [FunctionalFact]
    public async Task GivenUnavailableSessionPersistence_WhenReading_ThenReturn503WithoutCiphertext()
    {
        var input = await Setup();
        Authorize(RegistrationApiFixture.Token(input.Identity));
        await using var context = fixture.Context();
        await context.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.vault_access_session RENAME TO fixture_unavailable_session");
        try
        {
            using var response = await Send(input.Handle);
            await Failure(response, HttpStatusCode.ServiceUnavailable);
        }
        finally { await context.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_session RENAME TO vault_access_session"); }
    }

    [FunctionalFact]
    public async Task GivenCorruptStoredPolicy_WhenReading_ThenFailClosed()
    {
        var input = await Setup("corruptPolicy");
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var response = await Send(input.Handle);
        await Failure(response, HttpStatusCode.ServiceUnavailable);
        Assert.DoesNotContain("not-json", await response.Content.ReadAsStringAsync());
    }

    private sealed class UnspecifiedLengthContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes("{}")).AsTask();
    }

    private async Task<(Guid Identity, Guid AccountId, string Handle, EncryptedEnvelope Details)> Setup(string state = "active")
    {
        var identity = Guid.NewGuid();
        var id = Guid.NewGuid();
        fixture.Users[Guid.NewGuid().ToString("N") + "@example.test"] = new(identity, "fixture-password");
        var handle = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.True(OpaqueAccessHandle.TryHash(handle, out var verifier));
        var details = new EncryptedEnvelope("cerberus-content-v1", 1, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "AAAAAAAAAAAAAAAA", "AQID", "AAAAAAAAAAAAAAAAAAAAAA");
        if (state != "missing")
        {
            await using var context = fixture.Context();
            var account = new Account { PublicId = id, HeimdallPublicId = identity, Revision = 3,
                PolicyRevision = state == "stale" ? 2 : 1, RevocationGeneration = state == "staleGeneration" ? 2 : 1,
                DetailsEnvelope = Encoding.UTF8.GetBytes("not-json"),
                RenewalEnabled = state != "disabled",
                RenewalInterval = state == "disabled" ? null : state == "corruptPolicy" ? TimeSpan.Zero : state == "configured" ? TimeSpan.FromSeconds(1) : TimeSpan.FromHours(24),
                State = state == "closing" ? AccountState.ClosurePending : AccountState.Active };
            context.Accounts.Add(account);
            await context.SaveChangesAsync();
            long? selected = null;
            if (state == "selected")
            {
                var profile = new ArturRios.Cerberus.Domain.Profiles.Profile { PublicId = Guid.NewGuid(), AccountId = account.Id };
                context.Profiles.Add(profile);
                await context.SaveChangesAsync();
                selected = profile.Id;
            }
            context.VaultAccessSessions.Add(new VaultAccessSession { AccountId = account.Id, HandleVerifier = verifier,
                ProfileId = state == "profile" ? 42 : selected, IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                ExpiresAt = state == "disabled" ? null : state == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddHours(1),
                Revoked = state == "revoked", PolicyRevision = state == "zeroPolicy" ? 0 : 1, RevocationGeneration = 1 });
            if (state == "erased") context.TerminalErasures.Add(new TerminalErasure { ResourceId = id, ResourceKind = "account", DeletedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
        }
        return (identity, id, handle, details);
    }

    private async Task<HttpResponseMessage> Send(string? handle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/accounts/me/offline-policy");
        if (handle is not null) request.Headers.Add(Header, handle);
        return await Gateway.Client.SendAsync(request);
    }
    private static async Task Failure(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var output = (await response.Content.ReadFromJsonAsync<DataOutput<JsonElement?>>())!;
        Assert.False(output.Success);
        Assert.Null(output.Data);
    }
}
