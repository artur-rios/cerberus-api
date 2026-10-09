using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArturRios.Cerberus.Command.Accounts;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Configuration.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class RegistrationHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenHarnessContentEnvelope_WhenRegistering_ThenAcceptAndPreserveEveryField()
    {
        var command = Request();
        var node = JsonSerializer.SerializeToNode(command, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        // Literal protocol harness worked envelope, independent of the server DTO.
        var envelope = JsonNode.Parse("""
            {"format":"cerberus-content-v1","keyEpoch":1,
             "keySalt":"ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8",
             "nonce":"AAECAwQFBgcICQoL","ciphertext":"xvJT8pCjlfo5xRIRpss",
             "tag":"S66V63rqlxPAHtHfeemDGg"}
            """)!;
        node["details"] = envelope.DeepClone();
        using var response = await Gateway.Client.PostAsJsonAsync("/api/accounts", node);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var context = fixture.Context();
        var account = await context.Accounts.SingleAsync(x => x.PublicId == command.AccountId);
        Assert.True(JsonNode.DeepEquals(envelope, JsonNode.Parse(account.DetailsEnvelope)));
    }

    [FunctionalFact]
    public async Task GivenNewAccount_WhenPostingRegistration_ThenBindIdentityAndPreserveCiphertextWithoutKeys()
    {
        var command = Request();
        using var response = await Gateway.Client.PostAsJsonAsync("/api/accounts", command);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadFromJsonAsync<DataOutput<RegisterAccountOutput>>();
        Assert.True(body!.Success);
        Assert.Equal(command.AccountId, body.Data!.Id);
        Assert.Equal("protection_required", body.Data.OnboardingState);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(command.Identity.Password, raw);
        Assert.DoesNotContain(command.Identity.Email, raw);
        Assert.DoesNotContain("heimdallPublicId", raw);
        await using var context = fixture.Context();
        var account = await context.Accounts.SingleAsync(x => x.PublicId == command.AccountId);
        Assert.Equal(fixture.Users[command.Identity.Email].Id, account.HeimdallPublicId);
        Assert.Equal(command.Details, JsonSerializer.Deserialize<EncryptedEnvelope>(account.DetailsEnvelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [FunctionalTheory]
    [InlineData("ownerId")]
    [InlineData("vaultPassword")]
    [InlineData("identityToken")]
    [InlineData("badEnvelope")]
    [InlineData("missingGuid")]
    public async Task GivenMalformedOrUnknownInput_WhenPosting_ThenRejectBeforeIdentityCreation(string invalid)
    {
        var command = Request();
        _ = Gateway.Client;
        var identityCalls = fixture.IdentityCalls;
        var node = JsonSerializer.SerializeToNode(command, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        if (invalid == "badEnvelope") node["details"]!["nonce"] = "bad";
        else if (invalid == "missingGuid") node["accountId"] = Guid.Empty.ToString();
        else node[invalid] = "untrusted";
        using var response = await Gateway.Client.PostAsJsonAsync("/api/accounts", node);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(fixture.Users.ContainsKey(command.Identity.Email));
        Assert.Equal(identityCalls, fixture.IdentityCalls);
        await using var pendingContext = fixture.Context();
        Assert.False(await pendingContext.RegistrationOperations.AnyAsync(x => x.OperationId == command.IdempotencyKey));
        await using var context = fixture.Context();
        Assert.False(await context.Accounts.AnyAsync(x => x.PublicId == command.AccountId));
    }

    [FunctionalTheory]
    [InlineData("unavailable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("forbidden", HttpStatusCode.Forbidden)]
    public async Task GivenRequiredIdentityFailure_WhenPosting_ThenFailClosedWithStableRedactedOutcome(string prefix, HttpStatusCode expected)
    {
        var command = Request(prefix);
        using var response = await Gateway.Client.PostAsJsonAsync("/api/accounts", command);
        Assert.Equal(expected, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(command.Identity.Password, raw);
        Assert.DoesNotContain(command.Identity.Email, raw);
        await using var context = fixture.Context();
        Assert.False(await context.Accounts.AnyAsync(x => x.PublicId == command.AccountId));
    }

    [FunctionalFact]
    public async Task GivenExistingIdentityWithoutOwnershipProof_WhenPosting_ThenNeverLinkByEmail()
    {
        var command = Request();
        fixture.Users[command.Identity.Email] = new(Guid.NewGuid(), "other-fixture-password");
        using var response = await Gateway.Client.PostAsJsonAsync("/api/accounts", command);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var context = fixture.Context();
        Assert.False(await context.Accounts.AnyAsync(x => x.PublicId == command.AccountId));
    }

    [FunctionalFact]
    public async Task GivenPendingMfa_WhenPosting_ThenRequireCompletionBeforeBinding()
    {
        var command = Request();
        fixture.Users[command.Identity.Email] = new(Guid.NewGuid(), command.Identity.Password, Mfa: true);
        using var response = await Gateway.Client.PostAsJsonAsync("/api/accounts", command);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var context = fixture.Context();
        Assert.False(await context.Accounts.AnyAsync(x => x.PublicId == command.AccountId));
    }

    [FunctionalFact]
    public async Task GivenExistingIdentityWithCompletedProof_WhenPosting_ThenBindOnlyProvedIdentity()
    {
        var command = Request();
        var identity = new RegistrationApiFixture.User(Guid.NewGuid(), "other-fixture-password", Mfa: true);
        fixture.Users[command.Identity.Email] = identity;
        Authorize(RegistrationApiFixture.Token(identity.Id));
        using var response = await Gateway.Client.PostAsJsonAsync("/api/accounts", command);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var context = fixture.Context();
        Assert.Equal(identity.Id, (await context.Accounts.SingleAsync(x => x.PublicId == command.AccountId)).HeimdallPublicId);
    }

    [FunctionalFact]
    public async Task GivenLocalPersistenceFailureAfterIdentityCreation_WhenRetrying_ThenReconcileWithoutDuplicateIdentity()
    {
        var command = Request();
        await fixture.RejectAccountInsertAsync(command.AccountId);
        try
        {
            using var failed = await Gateway.Client.PostAsJsonAsync("/api/accounts", command);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.True(fixture.Users.ContainsKey(command.Identity.Email));
            await using var pending = fixture.Context();
            Assert.Null((await pending.RegistrationOperations.SingleAsync(x => x.OperationId == command.IdempotencyKey)).CompletedIdentityId);
            Assert.False(await pending.Accounts.AnyAsync(x => x.PublicId == command.AccountId));
        }
        finally { await fixture.AllowAccountInsertAsync(); }
        var originalIdentity = fixture.Users[command.Identity.Email].Id;
        using var resumed = await Gateway.Client.PostAsJsonAsync("/api/accounts", command);
        Assert.Equal(HttpStatusCode.Created, resumed.StatusCode);
        using var replay = await Gateway.Client.PostAsJsonAsync("/api/accounts", command);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(originalIdentity, fixture.Users[command.Identity.Email].Id);
        await using var after = fixture.Context();
        Assert.Equal(1, await after.Accounts.CountAsync(x => x.HeimdallPublicId == originalIdentity));
    }

    [FunctionalFact]
    public async Task GivenConcurrentIdenticalRegistration_WhenPosting_ThenReturnOneCreatedOutcomeAndIdempotentReplays()
    {
        var command = Request();
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Gateway.Client.PostAsJsonAsync("/api/accounts", command)));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Equal(5, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            await using var context = fixture.Context();
            Assert.Equal(1, await context.Accounts.CountAsync(x => x.PublicId == command.AccountId));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    private static RegisterAccountCommand Request(string prefix = "owner") => new()
    {
        AccountId = Guid.NewGuid(), IdempotencyKey = Guid.NewGuid(),
        Identity = new HeimdallRegistration("Fixture Owner", prefix + "-" + Guid.NewGuid().ToString("N") + "@example.test", "fixture-password"),
        Details = new EncryptedEnvelope("cerberus-content-v1", 1, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "AAAAAAAAAAAAAAAA", "AQID", "AAAAAAAAAAAAAAAAAAAAAA")
    };

    [FunctionalTheory]
    [InlineData("\"keyEpoch\":1", "\"keyEpoch\":\"1\"")]
    [InlineData("\"keyEpoch\":1", "\"keyEpoch\":9007199254740992")]
    [InlineData("\"keyEpoch\":1", "\"KeyEpoch\":1")]
    public async Task GivenNonProtocolEpoch_WhenPosting_ThenRejectBeforeSideEffects(string original, string replacement)
    {
        var command = Request();
        _ = Gateway.Client;
        var identityCalls = fixture.IdentityCalls;
        var raw = JsonSerializer.Serialize(command, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Replace(original, replacement);
        using var response = await Gateway.Client.PostAsync("/api/accounts", new StringContent(raw, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(fixture.Users.ContainsKey(command.Identity.Email));
        Assert.Equal(identityCalls, fixture.IdentityCalls);
        await using var pendingContext = fixture.Context();
        Assert.False(await pendingContext.RegistrationOperations.AnyAsync(x => x.OperationId == command.IdempotencyKey));
    }

    [FunctionalTheory]
    [InlineData("accountId")]
    [InlineData("idempotencyKey")]
    public async Task GivenNonCanonicalGuid_WhenPosting_ThenRejectBeforeSideEffects(string field)
    {
        var command = Request();
        _ = Gateway.Client;
        var identityCalls = fixture.IdentityCalls;
        var node = JsonSerializer.SerializeToNode(command, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        node[field] = "527A1001-8EF5-4C9B-A565-111111111111";
        using var response = await Gateway.Client.PostAsJsonAsync("/api/accounts", node);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(fixture.Users.ContainsKey(command.Identity.Email));
        Assert.Equal(identityCalls, fixture.IdentityCalls);
        await using var pendingContext = fixture.Context();
        Assert.False(await pendingContext.RegistrationOperations.AnyAsync(x => x.OperationId == command.IdempotencyKey));
    }

    [FunctionalTheory]
    [InlineData("accountId")]
    [InlineData("nonce")]
    [InlineData("password")]
    public async Task GivenDuplicateJsonMembers_WhenPosting_ThenRejectBeforeSideEffects(string member)
    {
        var command = Request();
        _ = Gateway.Client;
        var identityCalls = fixture.IdentityCalls;
        var value = member switch { "accountId" => command.AccountId.ToString(), "nonce" => command.Details.Nonce, _ => command.Identity.Password };
        var raw = JsonSerializer.Serialize(command, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        raw = raw.Replace("\"" + member + "\":", "\"" + member + "\":\"" + value + "\",\"" + member + "\":");
        using var response = await Gateway.Client.PostAsync("/api/accounts", new StringContent(raw, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(fixture.Users.ContainsKey(command.Identity.Email));
        Assert.Equal(identityCalls, fixture.IdentityCalls);
        await using var pendingContext = fixture.Context();
        Assert.False(await pendingContext.RegistrationOperations.AnyAsync(x => x.OperationId == command.IdempotencyKey));
    }
}
