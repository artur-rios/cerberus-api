using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Command.Accounts;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Query.Accounts;
using ArturRios.Configuration.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class AccountUpdateHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Header = "X-Cerberus-Vault-Access";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static EncryptedEnvelope Envelope(string ciphertext = "BAUG") =>
        new("cerberus-content-v1", 1, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "AAAAAAAAAAAAAAAA", ciphertext, "AAAAAAAAAAAAAAAAAAAAAA");

    [FunctionalFact]
    public async Task GivenCurrentOwnerAndAccess_WhenUpdating_ThenReadNewEnvelopeAndPreserveIdentityLink()
    {
        var input = await Setup();
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var response = await Send(input.Handle);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var result = (await response.Content.ReadFromJsonAsync<DataOutput<UpdateAccountOutput>>())!;
        Assert.True(result.Success);
        Assert.Equal(input.Id, result.Data!.Id);
        Assert.Equal(4, result.Data.Revision);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("heimdall", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("details", raw);
        Assert.DoesNotContain(input.Handle, raw);
        using var read = new HttpRequestMessage(HttpMethod.Get, "/api/accounts/me");
        read.Headers.Add(Header, input.Handle);
        using var current = await Gateway.Client.SendAsync(read);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var account = (await current.Content.ReadFromJsonAsync<DataOutput<AccountOutput>>())!.Data!;
        Assert.Equal(Envelope(), account.Details);
        Assert.Equal(4, account.Revision);
        await AssertStored(input.Id, input.Identity, 4, "BAUG");
    }

    [FunctionalFact]
    public async Task GivenNoIdentity_WhenUpdating_ThenRequireAuthenticationWithoutMutation()
    {
        var input = await Setup();
        using var response = await Send(input.Handle);
        await Failure(response, HttpStatusCode.Unauthorized);
        await AssertStored(input.Id, input.Identity);
    }

    [FunctionalFact]
    public async Task GivenNoAccessHeader_WhenUpdating_ThenRequireSeparateVaultAccess()
    {
        var input = await Setup();
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var response = await Send(null);
        await Failure(response, HttpStatusCode.Unauthorized);
        await AssertStored(input.Id, input.Identity);
    }

    [FunctionalFact]
    public async Task GivenOtherOwnersAccessHandle_WhenUpdating_ThenDenyAndPreserveBothAccounts()
    {
        var input = await Setup();
        var other = await Setup();
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var response = await Send(other.Handle);
        await Failure(response, HttpStatusCode.Forbidden);
        Assert.DoesNotContain(other.Id.ToString(), await response.Content.ReadAsStringAsync());
        await AssertStored(input.Id, input.Identity);
        await AssertStored(other.Id, other.Identity);
    }

    [FunctionalTheory]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("closing", HttpStatusCode.NotFound)]
    [InlineData("erased", HttpStatusCode.NotFound)]
    [InlineData("profile", HttpStatusCode.Forbidden)]
    [InlineData("expired", HttpStatusCode.Forbidden)]
    [InlineData("revoked", HttpStatusCode.Forbidden)]
    [InlineData("stale", HttpStatusCode.Forbidden)]
    public async Task GivenHiddenAccountOrInvalidAccess_WhenUpdating_ThenDenyWithoutMutation(string state, HttpStatusCode status)
    {
        var input = await Setup(state);
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var response = await Send(input.Handle);
        await Failure(response, status);
        if (state != "missing") await AssertStored(input.Id, input.Identity);
    }

    [FunctionalTheory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("stringRevision")]
    [InlineData("zeroRevision")]
    [InlineData("nullEnvelope")]
    [InlineData("unsupported")]
    [InlineData("plaintext")]
    [InlineData("owner")]
    [InlineData("query")]
    [InlineData("duplicateHeader")]
    [InlineData("malformedHeader")]
    [InlineData("emptyHeader")]
    public async Task GivenInvalidVisibleInput_WhenUpdating_ThenRejectWithoutMutation(string invalid)
    {
        var input = await Setup();
        Authorize(RegistrationApiFixture.Token(input.Identity));
        var body = JsonSerializer.Serialize(new { expectedRevision = 3, details = Envelope() }, Json);
        body = invalid switch
        {
            "unknown" => body.Insert(1, "\"other\":true,"),
            "duplicate" => body.Insert(1, "\"expectedRevision\":3,"),
            "stringRevision" => body.Replace("\"expectedRevision\":3", "\"expectedRevision\":\"3\""),
            "zeroRevision" => body.Replace("\"expectedRevision\":3", "\"expectedRevision\":0"),
            "nullEnvelope" => "{\"expectedRevision\":3,\"details\":null}",
            "unsupported" => body.Replace("cerberus-content-v1", "unsupported"),
            "plaintext" => body.Replace("\"format\":", "\"plaintext\":\"secret\",\"format\":"),
            "owner" => body.Insert(1, "\"identityId\":\"" + Guid.NewGuid() + "\","),
            _ => body
        };
        if (invalid == "emptyHeader")
        {
            using var host = new WebApplicationFactory<Program>();
            var bytes = Encoding.UTF8.GetBytes(body);
            var raw = await host.Server.SendAsync(context =>
            {
                context.Request.Method = "PUT";
                context.Request.Path = "/api/accounts/me";
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = bytes.Length;
                context.Request.Body = new MemoryStream(bytes);
                context.Request.Headers.Authorization = "Bearer " + RegistrationApiFixture.Token(input.Identity);
                context.Request.Headers[Header] = "";
            });
            Assert.Equal(400, raw.Response.StatusCode);
            Assert.Contains("no-store", raw.Response.Headers.CacheControl.ToString());
            var result = (await JsonSerializer.DeserializeAsync<DataOutput<UpdateAccountOutput>>(raw.Response.Body, Json))!;
            Assert.False(result.Success);
            Assert.Null(result.Data);
        }
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "/api/accounts/me" + (invalid == "query" ? "?owner=untrusted" : ""))
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation(Header, invalid == "duplicateHeader" ? new[] { input.Handle, input.Handle }
                : new[] { invalid == "malformedHeader" ? "bad" : input.Handle });
            using var response = await Gateway.Client.SendAsync(request);
            await Failure(response, HttpStatusCode.BadRequest);
        }
        await AssertStored(input.Id, input.Identity);
    }

    [FunctionalFact]
    public async Task GivenTwoSameRevisionUpdates_WhenCompeting_ThenOneWinsAndOldRevisionCannotReplay()
    {
        var input = await Setup();
        Authorize(RegistrationApiFixture.Token(input.Identity));
        var results = await Task.WhenAll(Send(input.Handle, "BAUG"), Send(input.Handle, "BwgJ"));
        using var first = results[0];
        using var second = results[1];
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        await Failure(results.Single(x => x.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict);
        var winning = first.StatusCode == HttpStatusCode.OK ? "BAUG" : "BwgJ";
        await AssertStored(input.Id, input.Identity, 4, winning);
        using var replay = await Send(input.Handle);
        await Failure(replay, HttpStatusCode.Conflict);
        await AssertStored(input.Id, input.Identity, 4, winning);
    }

    [FunctionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenCurrentIdentityDeniedOrUnavailable_WhenUpdating_ThenFailClosed(bool unavailable)
    {
        var input = await Setup();
        var pair = fixture.Users.Single(x => x.Value.Id == input.Identity);
        fixture.Users[pair.Key] = pair.Value with { Deleted = !unavailable, IdentityUnavailable = unavailable };
        Authorize(RegistrationApiFixture.Token(input.Identity));
        using var response = await Send(input.Handle);
        await Failure(response, unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Unauthorized);
        await AssertStored(input.Id, input.Identity);
    }

    [FunctionalFact]
    public async Task GivenUnavailableAccountPersistence_WhenUpdating_ThenReturn503WithoutMutation()
    {
        var input = await Setup();
        Authorize(RegistrationApiFixture.Token(input.Identity));
        await using var context = fixture.Context();
        await context.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.account RENAME TO fixture_unavailable_account");
        try
        {
            using var response = await Send(input.Handle);
            await Failure(response, HttpStatusCode.ServiceUnavailable);
        }
        finally { await context.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_account RENAME TO account"); }
        await AssertStored(input.Id, input.Identity);
    }

    private async Task<(Guid Id, Guid Identity, string Handle)> Setup(string state = "active")
    {
        var id = Guid.NewGuid();
        var identity = Guid.NewGuid();
        fixture.Users[Guid.NewGuid().ToString("N") + "@example.test"] = new(identity, "fixture-password");
        var handle = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.True(OpaqueAccessHandle.TryHash(handle, out var verifier));
        if (state != "missing")
        {
            await using var context = fixture.Context();
            var account = new Account { PublicId = id, HeimdallPublicId = identity, Revision = 3,
                DetailsEnvelope = JsonSerializer.SerializeToUtf8Bytes(Envelope("AQID"), Json),
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
        return (id, identity, handle);
    }

    private async Task<HttpResponseMessage> Send(string? handle, string ciphertext = "BAUG")
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/accounts/me")
        { Content = JsonContent.Create(new { expectedRevision = 3, details = Envelope(ciphertext) }) };
        if (handle is not null) request.Headers.Add(Header, handle);
        return await Gateway.Client.SendAsync(request);
    }
    private async Task AssertStored(Guid id, Guid identity, long revision = 3, string ciphertext = "AQID")
    {
        await using var context = fixture.Context();
        var account = await context.Accounts.SingleAsync(x => x.PublicId == id);
        Assert.Equal(identity, account.HeimdallPublicId);
        Assert.Equal(revision, account.Revision);
        Assert.Equal(Envelope(ciphertext), JsonSerializer.Deserialize<EncryptedEnvelope>(account.DetailsEnvelope, Json));
        Assert.Equal(1, account.PolicyRevision);
        Assert.Equal(1, account.RevocationGeneration);
    }
    private static async Task Failure(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var output = (await response.Content.ReadFromJsonAsync<DataOutput<UpdateAccountOutput>>())!;
        Assert.False(output.Success);
        Assert.Null(output.Data);
    }
}
