using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Command.Identity;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Configuration.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class IdentityUpdateHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Header = "X-Cerberus-Vault-Access";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [FunctionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenCurrentOwner_WhenUpdatingIdentity_ThenBindOriginalActorAndPreserveVault(bool changeEmail)
    {
        var input = await Setup();
        var token = RegistrationApiFixture.Token(input.Actor);
        Authorize(token);
        var email = changeEmail ? Guid.NewGuid().ToString("N") + "@example.test" : input.Email;
        using var response = await Send(input.Handle, "Updated Name", email);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var result = (await response.Content.ReadFromJsonAsync<DataOutput<UpdateIdentityOutput>>())!;
        Assert.True(result.Success);
        Assert.Equal(input.Actor, result.Data!.Id);
        Assert.Equal("Updated Name", result.Data.Name);
        Assert.Equal(email, result.Data.Email);
        Assert.Equal(!changeEmail, result.Data.EmailVerified);
        var capture = fixture.IdentityUpdates[input.Actor];
        Assert.Equal("Bearer " + token, capture.Bearer);
        using var sent = JsonDocument.Parse(capture.Body);
        Assert.Equal(new[] { "email", "name" }, sent.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(email, sent.RootElement.GetProperty("email").GetString());
        using var returned = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "email", "emailVerified", "id", "name" }, returned.RootElement.GetProperty("data").EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.DoesNotContain(input.Handle, await response.Content.ReadAsStringAsync());
        Assert.Equal(input.Before, await Snapshot(input.Actor));
    }

    [FunctionalTheory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("foreignScope", HttpStatusCode.Unauthorized)]
    [InlineData("missingAccess", HttpStatusCode.Unauthorized)]
    [InlineData("foreignAccess", HttpStatusCode.Forbidden)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("closing", HttpStatusCode.NotFound)]
    [InlineData("erased", HttpStatusCode.NotFound)]
    [InlineData("profile", HttpStatusCode.Forbidden)]
    [InlineData("expired", HttpStatusCode.Forbidden)]
    [InlineData("revoked", HttpStatusCode.Forbidden)]
    [InlineData("stale", HttpStatusCode.Forbidden)]
    [InlineData("deletedIdentity", HttpStatusCode.Unauthorized)]
    [InlineData("unavailableIdentity", HttpStatusCode.ServiceUnavailable)]
    public async Task GivenDeniedAccess_WhenUpdatingIdentity_ThenNeverCallProviderOrChangeVault(string state, HttpStatusCode status)
    {
        var input = await Setup(state);
        if (state != "anonymous") Authorize(RegistrationApiFixture.Token(input.Actor, state == "foreignScope" ? Guid.NewGuid() : null));
        var handle = state == "foreignAccess" ? (await Setup()).Handle : state == "missingAccess" ? null : input.Handle;
        var calls = fixture.IdentityUpdateCalls;
        using var response = await Send(handle);
        await Failure(response, status);
        Assert.Equal(calls, fixture.IdentityUpdateCalls);
        Assert.Equal(input.Before, await Snapshot(input.Actor));
    }

    [FunctionalTheory]
    [InlineData("unknown")]
    [InlineData("owner")]
    [InlineData("role")]
    [InlineData("scope")]
    [InlineData("password")]
    [InlineData("vault")]
    [InlineData("duplicate")]
    [InlineData("case")]
    [InlineData("numericName")]
    [InlineData("nullName")]
    [InlineData("emptyName")]
    [InlineData("longName")]
    [InlineData("missingEmail")]
    [InlineData("badEmail")]
    [InlineData("query")]
    [InlineData("duplicateHeader")]
    [InlineData("badHeader")]
    [InlineData("emptyHeader")]
    public async Task GivenInvalidInput_WhenUpdatingIdentity_ThenReturn400BeforeProvider(string invalid)
    {
        var input = await Setup();
        Authorize(RegistrationApiFixture.Token(input.Actor));
        var body = invalid switch
        {
            "owner" => "{\"identityId\":\"" + Guid.NewGuid() + "\",\"name\":\"New\",\"email\":\"new@example.test\"}",
            "role" => "{\"roleId\":2,\"name\":\"New\",\"email\":\"new@example.test\"}",
            "scope" => "{\"scopeId\":\"" + Guid.NewGuid() + "\",\"name\":\"New\",\"email\":\"new@example.test\"}",
            "password" => "{\"password\":\"secret\",\"name\":\"New\",\"email\":\"new@example.test\"}",
            "vault" => "{\"details\":{},\"name\":\"New\",\"email\":\"new@example.test\"}",
            "unknown" => "{\"other\":true,\"name\":\"New\",\"email\":\"new@example.test\"}",
            "duplicate" => "{\"name\":\"First\",\"name\":\"New\",\"email\":\"new@example.test\"}",
            "case" => "{\"Name\":\"New\",\"email\":\"new@example.test\"}",
            "numericName" => "{\"name\":42,\"email\":\"new@example.test\"}",
            "nullName" => "{\"name\":null,\"email\":\"new@example.test\"}",
            "emptyName" => "{\"name\":\" \",\"email\":\"new@example.test\"}",
            "longName" => JsonSerializer.Serialize(new { name = new string('x', 201), email = "new@example.test" }),
            "missingEmail" => "{\"name\":\"New\"}",
            "badEmail" => "{\"name\":\"New\",\"email\":\"invalid\"}",
            _ => "{\"name\":\"New\",\"email\":\"new@example.test\"}"
        };
        var calls = fixture.IdentityUpdateCalls;
        if (invalid == "emptyHeader")
        {
            using var host = new WebApplicationFactory<Program>();
            var bytes = Encoding.UTF8.GetBytes(body);
            var raw = await host.Server.SendAsync(context =>
            {
                context.Request.Method = "PUT"; context.Request.Path = "/api/identity/me";
                context.Request.ContentType = "application/json"; context.Request.ContentLength = bytes.Length;
                context.Request.Body = new MemoryStream(bytes);
                context.Request.Headers.Authorization = "Bearer " + RegistrationApiFixture.Token(input.Actor);
                context.Request.Headers[Header] = "";
            });
            Assert.Equal(400, raw.Response.StatusCode);
            Assert.Contains("no-store", raw.Response.Headers.CacheControl.ToString());
            var result = (await JsonSerializer.DeserializeAsync<DataOutput<UpdateIdentityOutput>>(raw.Response.Body, Json))!;
            Assert.False(result.Success); Assert.Null(result.Data);
        }
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "/api/identity/me" + (invalid == "query" ? "?owner=untrusted" : ""))
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation(Header, invalid == "duplicateHeader" ? new[] { input.Handle, input.Handle }
                : new[] { invalid == "badHeader" ? "invalid" : input.Handle });
            using var response = await Gateway.Client.SendAsync(request);
            await Failure(response, HttpStatusCode.BadRequest);
        }
        Assert.Equal(calls, fixture.IdentityUpdateCalls);
        Assert.Equal(input.Before, await Snapshot(input.Actor));
    }

    [FunctionalTheory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task GivenProviderFailure_WhenUpdatingIdentity_ThenMapStableStatusAndPreserveVault(int status)
    {
        var input = await Setup();
        Authorize(RegistrationApiFixture.Token(input.Actor));
        using var response = await Send(input.Handle, "provider-" + status);
        await Failure(response, (HttpStatusCode)(status >= 500 ? 503 : status));
        Assert.DoesNotContain("private-provider-error", await response.Content.ReadAsStringAsync());
        Assert.Equal(input.Before, await Snapshot(input.Actor));
    }

    [FunctionalFact]
    public async Task GivenDuplicateEmail_WhenUpdatingIdentity_ThenReturnConflictAndPreserveBothOwners()
    {
        var input = await Setup(); var other = await Setup();
        Authorize(RegistrationApiFixture.Token(input.Actor));
        using var response = await Send(input.Handle, email: other.Email);
        await Failure(response, HttpStatusCode.Conflict);
        Assert.True(fixture.Users.ContainsKey(input.Email));
        Assert.Equal(other.Actor, fixture.Users[other.Email].Id);
        Assert.Equal(input.Before, await Snapshot(input.Actor));
        Assert.Equal(other.Before, await Snapshot(other.Actor));
    }

    [FunctionalFact]
    public async Task GivenLostResponseAfterProviderCommit_WhenRetryingReplacement_ThenNoVaultMutationOrFalseRollback()
    {
        var input = await Setup(); Authorize(RegistrationApiFixture.Token(input.Actor));
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        using var lost = await Send(input.Handle, "response-loss", email);
        await Failure(lost, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(input.Actor, fixture.Users[email].Id);
        Assert.Equal(input.Before, await Snapshot(input.Actor));
        using var retry = await Send(input.Handle, "Recovered Name", email);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.True(retry.Headers.CacheControl?.NoStore);
        Assert.Equal("Recovered Name", fixture.Users[email].Name);
        Assert.False(fixture.Users[email].EmailVerified);
        Assert.Equal(input.Before, await Snapshot(input.Actor));
    }

    [FunctionalFact]
    public async Task GivenUnavailableDatabase_WhenUpdatingIdentity_ThenReturn503BeforeProvider()
    {
        var input = await Setup(); Authorize(RegistrationApiFixture.Token(input.Actor));
        var calls = fixture.IdentityUpdateCalls;
        await using var context = fixture.Context();
        await context.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.account RENAME TO fixture_identity_unavailable_account");
        try { using var response = await Send(input.Handle); await Failure(response, HttpStatusCode.ServiceUnavailable); }
        finally { await context.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_identity_unavailable_account RENAME TO account"); }
        Assert.Equal(calls, fixture.IdentityUpdateCalls);
        Assert.Equal(input.Before, await Snapshot(input.Actor));
    }

    private async Task<(Guid Actor, string Email, string Handle, string Before)> Setup(string state = "active")
    {
        var actor = Guid.NewGuid(); var email = Guid.NewGuid().ToString("N") + "@example.test";
        fixture.Users[email] = new(actor, "fixture-password", Deleted: state == "deletedIdentity", IdentityUnavailable: state == "unavailableIdentity", Email: email);
        var handle = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.True(OpaqueAccessHandle.TryHash(handle, out var verifier));
        if (state != "missing")
        {
            await using var context = fixture.Context();
            var account = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = actor, Revision = 3,
                DetailsEnvelope = JsonSerializer.SerializeToUtf8Bytes(new EncryptedEnvelope("cerberus-content-v1", 1, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "AAAAAAAAAAAAAAAA", "AQID", "AAAAAAAAAAAAAAAAAAAAAA"), Json),
                State = state == "closing" ? AccountState.ClosurePending : AccountState.Active };
            context.Accounts.Add(account); await context.SaveChangesAsync();
            context.VaultAccessSessions.Add(new VaultAccessSession { AccountId = account.Id, HandleVerifier = verifier,
                ProfileId = state == "profile" ? 42 : null, IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                ExpiresAt = state == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddHours(1),
                Revoked = state == "revoked", PolicyRevision = state == "stale" ? 0 : 1, RevocationGeneration = 1 });
            if (state == "erased") context.TerminalErasures.Add(new TerminalErasure { ResourceId = account.PublicId, ResourceKind = "account", DeletedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
        }
        return (actor, email, handle, await Snapshot(actor));
    }
    private async Task<string> Snapshot(Guid actor)
    {
        await using var context = fixture.Context();
        var accounts = await context.Accounts.AsNoTracking().Where(x => x.HeimdallPublicId == actor).ToListAsync();
        var ids = accounts.Select(x => x.Id).ToArray();
        return JsonSerializer.Serialize(new { accounts, sessions = await context.VaultAccessSessions.AsNoTracking().Where(x => ids.Contains(x.AccountId)).OrderBy(x => x.Id).ToListAsync() });
    }
    private async Task<HttpResponseMessage> Send(string? handle, string name = "New Name", string email = "new@example.test")
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/identity/me") { Content = JsonContent.Create(new { name, email }) };
        if (handle is not null) request.Headers.Add(Header, handle);
        return await Gateway.Client.SendAsync(request);
    }
    private static async Task Failure(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var result = (await response.Content.ReadFromJsonAsync<DataOutput<UpdateIdentityOutput>>())!;
        Assert.False(result.Success); Assert.Null(result.Data);
    }
}
