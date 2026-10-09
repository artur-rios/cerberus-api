using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArturRios.Cerberus.Command.Authentication;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Configuration.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class AuthenticationHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenScopedIdentity_WhenLoggingIn_ThenReturnOnlyPermittedPublicContextWithoutVaultAccess(bool bound)
    {
        var input = User();
        var account = bound ? await Bind(input.Email) : null;
        using var response = await Gateway.Client.PostAsJsonAsync("/api/auth/login", input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var result = (await response.Content.ReadFromJsonAsync<DataOutput<AuthenticationOutput>>())!;
        Assert.True(result.Success);
        Assert.False(result.Data!.Identity.RequiresTwoFactor);
        Assert.NotNull(result.Data.Identity.Token);
        Assert.Equal(account, result.Data.Account);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(input.Password, raw);
        Assert.DoesNotContain(input.Email, raw);
        Assert.DoesNotContain("vaultSession", raw);
        Assert.DoesNotContain("detailsEnvelope", raw);
        Assert.DoesNotContain("heimdallPublicId", raw);
        if (!bound)
        {
            await using var context = fixture.Context();
            Assert.False(await context.Accounts.AnyAsync(x => x.HeimdallPublicId == fixture.Users[input.Email].Id));
        }
    }

    [FunctionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenMfaIdentity_WhenCompletingChallenge_ThenReturnContextOnlyAfterVerificationAndRejectReplay(bool recovery)
    {
        var input = User(mfa: true);
        var account = await Bind(input.Email);
        using var pending = await Gateway.Client.PostAsJsonAsync("/api/auth/login", input);
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        var challenge = (await pending.Content.ReadFromJsonAsync<DataOutput<AuthenticationOutput>>())!.Data!;
        Assert.True(challenge.Identity.RequiresTwoFactor);
        Assert.Null(challenge.Identity.Token);
        Assert.Null(challenge.Account);
        var completion = new VerifyChallengeCommand { ChallengeToken = challenge.Identity.ChallengeToken!, Code = recovery ? null : "123456", RecoveryCode = recovery ? "fixture-recovery-code" : null };
        using var verified = await Gateway.Client.PostAsJsonAsync("/api/auth/2fa/verify", completion);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        Assert.True(verified.Headers.CacheControl?.NoStore);
        var result = (await verified.Content.ReadFromJsonAsync<DataOutput<AuthenticationOutput>>())!.Data!;
        Assert.False(result.Identity.RequiresTwoFactor);
        Assert.NotNull(result.Identity.Token);
        Assert.Equal(account, result.Account);
        using var replay = await Gateway.Client.PostAsJsonAsync("/api/auth/2fa/verify", completion);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenMissingIdentityOrWrongPassword_WhenLoggingIn_ThenReturnSameGenericDenial()
    {
        var existing = User();
        using var missing = await Gateway.Client.PostAsJsonAsync("/api/auth/login", new LoginCommand { Email = Guid.NewGuid().ToString("N") + "@example.test", Password = "fixture-password" });
        using var wrong = await Gateway.Client.PostAsJsonAsync("/api/auth/login", new LoginCommand { Email = existing.Email, Password = "wrong-fixture-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var missingBody = JsonNode.Parse(await missing.Content.ReadAsStringAsync())!.AsObject();
        var wrongBody = JsonNode.Parse(await wrong.Content.ReadAsStringAsync())!.AsObject();
        // ProcessOutput records each response's creation time; all other fields must agree.
        Assert.True(missingBody.Remove("timestamp"));
        Assert.True(wrongBody.Remove("timestamp"));
        Assert.True(JsonNode.DeepEquals(missingBody, wrongBody));
        Assert.True(wrong.Headers.CacheControl?.NoStore);
    }

    [FunctionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenInactiveOrErasedAccount_WhenLoggingIn_ThenDenyWithoutBearer(bool erased)
    {
        var input = User();
        await Bind(input.Email, erased ? AccountState.Active : AccountState.ClosurePending, erased);
        using var response = await Gateway.Client.PostAsJsonAsync("/api/auth/login", input);
        await AssertFailure(response, HttpStatusCode.Unauthorized);
    }

    [FunctionalTheory]
    [InlineData("unavailable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("forbidden-login", HttpStatusCode.Forbidden)]
    [InlineData("malformed", HttpStatusCode.ServiceUnavailable)]
    [InlineData("foreign", HttpStatusCode.Unauthorized)]
    [InlineData("deleted", HttpStatusCode.Unauthorized)]
    [InlineData("current-unavailable", HttpStatusCode.ServiceUnavailable)]
    public async Task GivenRequiredIdentityFailure_WhenLoggingIn_ThenFailClosedWithRedactedStatus(string kind, HttpStatusCode status)
    {
        var input = User(prefix: kind);
        if (kind is "deleted" or "current-unavailable") fixture.Users[input.Email] = fixture.Users[input.Email] with { Deleted = kind == "deleted", IdentityUnavailable = kind == "current-unavailable" };
        using var response = await Gateway.Client.PostAsJsonAsync("/api/auth/login", input);
        await AssertFailure(response, status);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(input.Email, raw);
        Assert.DoesNotContain(input.Password, raw);
    }

    [FunctionalTheory]
    [InlineData("/api/auth/login", "{\"email\":\"a@example.test\",\"password\":\"fixture-password\",\"scopeId\":\"527a1001-8ef5-4c9b-a565-111111111111\"}")]
    [InlineData("/api/auth/login", "{\"email\":\"a@example.test\",\"password\":\"first\",\"password\":\"second\"}")]
    [InlineData("/api/auth/login", "{\"email\":\"a@example.test\",\"password\":12345678}")]
    [InlineData("/api/auth/login", "{\"email\":\"bad\",\"password\":\"fixture-password\"}")]
    [InlineData("/api/auth/2fa/verify", "{\"challengeToken\":\"fixture\",\"code\":\"123456\",\"vaultPassword\":\"forbidden\"}")]
    [InlineData("/api/auth/2fa/verify", "{\"challengeToken\":\"\",\"code\":\"123456\"}")]
    [InlineData("/api/auth/2fa/verify", "{\"challengeToken\":\"fixture\",\"code\":\"123456\",\"recoveryCode\":\"fixture\"}")]
    [InlineData("/api/auth/2fa/verify", "{\"challengeToken\":\"fixture\"}")]
    [InlineData("/api/auth/2fa/verify", "{\"challengeToken\":\"fixture\",\"code\":\"123456\",\"code\":\"654321\"}")]
    public async Task GivenInvalidOrUnknownInput_WhenAuthenticating_ThenRejectWithoutDependencyCalls(string route, string json)
    {
        _ = Gateway.Client;
        var calls = fixture.IdentityCalls;
        using var response = await Gateway.Client.PostAsync(route, new StringContent(json, Encoding.UTF8, "application/json"));
        await AssertFailure(response, HttpStatusCode.BadRequest);
        Assert.Equal(calls, fixture.IdentityCalls);
    }

    [FunctionalFact]
    public async Task GivenUnavailableAccountPersistence_WhenLoggingIn_ThenReturn503WithoutBearer()
    {
        var input = User();
        await using var context = fixture.Context();
        await context.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.account RENAME TO fixture_unavailable_account");
        try
        {
            using var response = await Gateway.Client.PostAsJsonAsync("/api/auth/login", input);
            await AssertFailure(response, HttpStatusCode.ServiceUnavailable);
        }
        finally { await context.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_account RENAME TO account"); }
    }

    private LoginCommand User(bool mfa = false, string prefix = "owner")
    {
        var input = new LoginCommand { Email = prefix + "-" + Guid.NewGuid().ToString("N") + "@example.test", Password = "fixture-password" };
        fixture.Users[input.Email] = new(Guid.NewGuid(), input.Password, mfa);
        return input;
    }
    private async Task<AuthenticationAccount> Bind(string email, AccountState state = AccountState.Active, bool erased = false)
    {
        var id = Guid.NewGuid();
        await using var context = fixture.Context();
        context.Accounts.Add(new Account { PublicId = id, HeimdallPublicId = fixture.Users[email].Id, DetailsEnvelope = [1, 2, 3], State = state, Revision = 3 });
        if (erased) context.TerminalErasures.Add(new TerminalErasure { ResourceId = id, ResourceKind = "account", DeletedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        return new(id, 3);
    }
    private static async Task AssertFailure(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var result = (await response.Content.ReadFromJsonAsync<DataOutput<AuthenticationOutput>>())!;
        Assert.False(result.Success);
        Assert.Null(result.Data);
    }
}
