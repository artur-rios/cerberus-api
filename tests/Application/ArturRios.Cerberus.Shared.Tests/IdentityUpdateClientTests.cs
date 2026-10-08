using System.Net;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Jwt;

namespace ArturRios.Cerberus.Shared.Tests;

public class IdentityUpdateClientTests
{
    private static readonly Guid Identity = Guid.Parse("527a1001-8ef5-4c9b-a565-222222222222");
    private const string Name = "Updated Owner";
    private const string Email = "updated@example.test";

    [UnitFact]
    public async Task GivenValidActor_WhenUpdatingIdentity_ThenUseOwnPublicRouteOriginalTokenAndOnlyPermittedFields()
    {
        var token = Token();
        var result = await Client(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/api/persons/" + Identity.ToString("D"), request.RequestUri!.AbsolutePath);
            Assert.Equal(token, request.Headers.Authorization?.Parameter);
            Assert.NotEqual(Options().HeimdallServiceCredential, request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(new[] { "email", "name" }, body.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
            Assert.Equal(Name, body.RootElement.GetProperty("name").GetString());
            Assert.Equal(Email, body.RootElement.GetProperty("email").GetString());
            return Response();
        }).UpdateIdentityAsync(token, Identity, Name, Email, default);
        Assert.Null(result.Error);
        Assert.Equal(Identity, result.Identity!.Id);
        Assert.Equal(Name, result.Identity.Name);
        Assert.Equal(Email, result.Identity.Email);
        Assert.False(result.Identity.EmailVerified);
        Assert.DoesNotContain("ScopeId", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("Role", JsonSerializer.Serialize(result));
    }

    [UnitTheory]
    [InlineData("invalid")]
    [InlineData("otherActor")]
    [InlineData("foreignScope")]
    public async Task GivenInvalidOrMismatchedActorToken_WhenUpdating_ThenRejectBeforeTransport(string invalid)
    {
        var token = invalid == "invalid" ? "not-a-token" : Token(invalid == "foreignScope" ? Guid.NewGuid() : null);
        var actor = invalid == "otherActor" ? Guid.NewGuid() : Identity;
        var client = Client((_, _) => throw new InvalidOperationException("Transport must not be called."));
        var result = await client.UpdateIdentityAsync(token, actor, Name, Email, default);
        Assert.Equal("authentication_required", result.Error);
        Assert.Null(result.Identity);
    }

    [UnitTheory]
    [InlineData(400, "validation_failed")]
    [InlineData(401, "authentication_required")]
    [InlineData(403, "identity_update_forbidden")]
    [InlineData(404, "not_found")]
    [InlineData(409, "identity_conflict")]
    [InlineData(500, "identity_unavailable")]
    [InlineData(503, "identity_unavailable")]
    public async Task GivenProviderRefusal_WhenUpdating_ThenMapStableErrorWithoutProviderPayload(int status, string error)
    {
        var result = await Client((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent("provider-private-details", Encoding.UTF8, "application/json") }))
            .UpdateIdentityAsync(Token(), Identity, Name, Email, default);
        Assert.Equal(error, result.Error);
        Assert.Null(result.Identity);
        Assert.DoesNotContain("provider-private", JsonSerializer.Serialize(result));
    }

    [UnitTheory]
    [InlineData("missingId")]
    [InlineData("missingName")]
    [InlineData("missingEmail")]
    [InlineData("missingVerified")]
    [InlineData("missingScope")]
    [InlineData("missingRole")]
    [InlineData("wrongId")]
    [InlineData("wrongScope")]
    [InlineData("wrongRole")]
    [InlineData("stringRole")]
    [InlineData("nullName")]
    [InlineData("nameMismatch")]
    [InlineData("emailMismatch")]
    [InlineData("duplicateData")]
    [InlineData("duplicateRoot")]
    [InlineData("invalidJson")]
    public async Task GivenIncompatibleProviderSuccess_WhenUpdating_ThenFailClosedWithoutForwardingIdentityData(string invalid)
    {
        var data = Data();
        var removed = invalid switch { "missingId" => "id", "missingName" => "name", "missingEmail" => "email",
            "missingVerified" => "emailVerified", "missingScope" => "scopeId", "missingRole" => "role", _ => null };
        if (removed is not null) data.Remove(removed);
        if (invalid == "wrongId") data["id"] = Guid.NewGuid();
        if (invalid == "wrongScope") data["scopeId"] = Guid.NewGuid();
        if (invalid == "wrongRole") data["role"] = 1;
        if (invalid == "stringRole") data["role"] = "3";
        if (invalid == "nullName") data["name"] = null;
        if (invalid == "nameMismatch") data["name"] = "another-person-private-name";
        if (invalid == "emailMismatch") data["email"] = "another-person@example.test";
        var body = JsonSerializer.Serialize(new { success = true, errors = Array.Empty<string>(), data });
        if (invalid == "duplicateData") body = body.Replace("\"emailVerified\":false", "\"emailVerified\":true,\"emailVerified\":false");
        if (invalid == "duplicateRoot") body = body.Replace("\"success\":true", "\"success\":false,\"success\":true");
        if (invalid == "invalidJson") body = "not-json";
        var result = await Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") }))
            .UpdateIdentityAsync(Token(), Identity, Name, Email, default);
        Assert.Equal("identity_unavailable", result.Error);
        Assert.Null(result.Identity);
    }

    [UnitFact]
    public async Task GivenProviderTimeout_WhenUpdating_ThenReturnUnavailableWithoutIdentityData()
    {
        var result = await Client((_, _) => throw new TaskCanceledException("provider-private-details"))
            .UpdateIdentityAsync(Token(), Identity, Name, Email, default);
        Assert.Equal("identity_unavailable", result.Error);
        Assert.Null(result.Identity);
    }

    [UnitFact]
    public async Task GivenCancelledCaller_WhenUpdating_ThenPropagateWithoutTransport()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client((_, _) => throw new InvalidOperationException())
            .UpdateIdentityAsync(Token(), Identity, Name, Email, cancelled.Token));
    }

    private static Dictionary<string, object?> Data() => new()
    {
        ["id"] = Identity, ["name"] = Name, ["email"] = Email, ["role"] = 3,
        ["scopeId"] = Options().HeimdallScopeId, ["emailVerified"] = false,
        ["ownedScopeIds"] = Array.Empty<Guid>(), ["createdAt"] = "2026-10-07T00:00:00Z", ["updatedAt"] = "2026-10-08T00:00:00Z"
    };
    private static CerberusOptions Options() => CerberusOptionsValidatorTests.ValidOptions();
    private static string Token(Guid? scope = null)
    {
        var options = Options();
        return new JwtHandler().CreateToken(new JwtConfiguration(60, options.AuthIssuer!, options.AuthAudience!, options.AuthValidationSecret!,
            new Dictionary<string, string> { ["id"] = Identity.ToString(), ["roleId"] = "3", ["scopeId"] = (scope ?? options.HeimdallScopeId).ToString() }));
    }
    private static HttpResponseMessage Response() => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { success = true, errors = Array.Empty<string>(), data = Data() }), Encoding.UTF8, "application/json") };
    private static HeimdallClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var options = Options();
        return new(new HttpClient(new Transport(handler)) { BaseAddress = new Uri(options.HeimdallBaseUrl!) }, options, new HeimdallTokenValidator(options));
    }
    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
