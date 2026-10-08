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
public class AccountReadHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Header = "X-Cerberus-Vault-Access";

    [FunctionalFact]
    public async Task GivenCurrentOwnerAndAccountWideAccess_WhenReading_ThenReturnExactOpaqueAccountWithoutIdentityData()
    {
        var input = await Setup();
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var response = await Send(input.Handle);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var result = (await response.Content.ReadFromJsonAsync<DataOutput<AccountOutput>>())!.Data!;
        Assert.Equal(input.AccountId, result.Id);
        Assert.Equal(3, result.Revision);
        Assert.Equal("active", result.State);
        Assert.Equal(input.Details, result.Details);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("heimdallPublicId", raw);
        Assert.DoesNotContain("accountId", raw);
        Assert.DoesNotContain("handleVerifier", raw);
        Assert.DoesNotContain("fixture-password", raw);
        Assert.DoesNotContain(input.Handle, raw);
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
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/accounts/me" + (invalid == "query" ? "?ownerId=" + Guid.NewGuid() : ""));
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
                context.Request.Path = "/api/accounts/me";
                context.Request.Headers.Authorization = "Bearer " + RegistrationApiFixture.Token(input.Identity);
                context.Request.Headers[Header] = "";
            });
            Assert.Equal(400, raw.Response.StatusCode);
            Assert.Contains("no-store", raw.Response.Headers.CacheControl.ToString());
            var result = (await JsonSerializer.DeserializeAsync<DataOutput<AccountOutput>>(raw.Response.Body,
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
    public async Task GivenCorruptStoredEnvelope_WhenReading_ThenReturn503WithoutReturningCorruption()
    {
        var input = await Setup("corrupt");
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
                DetailsEnvelope = state == "corrupt" ? Encoding.UTF8.GetBytes("not-json") : JsonSerializer.SerializeToUtf8Bytes(details, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                State = state == "closing" ? AccountState.ClosurePending : AccountState.Active };
            context.Accounts.Add(account);
            await context.SaveChangesAsync();
            context.VaultAccessSessions.Add(new VaultAccessSession { AccountId = account.Id, HandleVerifier = verifier,
                ProfileId = state == "profile" ? 42 : null, IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                ExpiresAt = state == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddHours(1),
                Revoked = state == "revoked", PolicyRevision = state == "stale" ? 0 : 1, RevocationGeneration = 1 });
            if (state == "erased") context.TerminalErasures.Add(new TerminalErasure { ResourceId = id, ResourceKind = "account", DeletedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
        }
        return (identity, id, handle, details);
    }

    private async Task<HttpResponseMessage> Send(string? handle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/accounts/me");
        if (handle is not null) request.Headers.Add(Header, handle);
        return await Gateway.Client.SendAsync(request);
    }
    private static async Task Failure(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var output = (await response.Content.ReadFromJsonAsync<DataOutput<AccountOutput>>())!;
        Assert.False(output.Success);
        Assert.Null(output.Data);
    }
}
