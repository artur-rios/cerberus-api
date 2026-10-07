using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Jwt;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ArturRios.Cerberus.Shared.Tests;

public sealed class HeimdallClientTests
{
    private static readonly Guid Identity = Guid.Parse("527a1001-8ef5-4c9b-a565-222222222222");

    [UnitFact]
    public async Task GivenScopedLogin_WhenCallingHeimdall_ThenBindScopeAndValidateReturnedToken()
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        var token = Token();
        var handler = new FixtureHandler(async request =>
        {
            Assert.Equal("/api/auth/login", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(options.HeimdallScopeId.ToString(), body.RootElement.GetProperty("scopeId").GetString());
            Assert.Equal("fixture-password", body.RootElement.GetProperty("password").GetString());
            return Envelope(new { token, expiresAt = DateTimeOffset.UtcNow.AddMinutes(5), emailVerified = true, requiresTwoFactor = false });
        });
        var result = await Client(handler).LoginAsync("fixture@example.test", "fixture-password", default);
        Assert.NotNull(result);
        Assert.Equal(token, result.Token);
        Assert.False(result.RequiresTwoFactor);
    }

    [UnitFact]
    public async Task GivenMfaChallenge_WhenLoggingIn_ThenNeverReturnAnAuthenticatedToken()
    {
        var result = await Client(new FixtureHandler(_ => Task.FromResult(Envelope(new
        {
            token = (string?)null,
            expiresAt = (DateTimeOffset?)null,
            emailVerified = (bool?)null,
            requiresTwoFactor = true,
            challengeToken = "opaque-challenge",
            availableMethods = new[] { "App" }
        })))).LoginAsync("fixture@example.test", "fixture-password", default);
        Assert.NotNull(result);
        Assert.True(result.RequiresTwoFactor);
        Assert.Null(result.Token);
        Assert.Equal("opaque-challenge", result.ChallengeToken);
    }

    [UnitTheory]
    [InlineData("not-json", 200)]
    [InlineData("{\"success\":false,\"errors\":[],\"data\":{\"token\":\"untrusted\"}}", 200)]
    [InlineData("{\"success\":true,\"errors\":[],\"data\":{\"token\":\"untrusted\"}}", 200)]
    [InlineData("upstream-secret", 401)]
    [InlineData("upstream-secret", 503)]
    public async Task GivenRejectedOrMalformedUpstream_WhenLoggingIn_ThenFailClosedWithoutEcho(string body, int status)
    {
        var result = await Client(new FixtureHandler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") })))
            .LoginAsync("fixture@example.test", "fixture-password", default);
        Assert.Null(result);
    }

    [UnitFact]
    public async Task GivenDependencyTimeout_WhenLoggingIn_ThenFailClosed()
    {
        var result = await Client(new FixtureHandler(_ => throw new TaskCanceledException("upstream-secret")))
            .LoginAsync("fixture@example.test", "fixture-password", default);
        Assert.Null(result);
    }

    [UnitFact]
    public async Task GivenDeletedCurrentIdentity_WhenRevalidating_ThenRejectDespiteValidJwt()
    {
        var result = await Client(new FixtureHandler(_ => Task.FromResult(Envelope(new
        { id = Identity, scopeId = CerberusOptionsValidatorTests.ValidOptions().HeimdallScopeId, role = 3, isDeleted = true }))))
            .RevalidateAsync(Token(), default);
        Assert.False(result);
    }

    [UnitFact]
    public async Task GivenActiveIdentity_WhenRevalidating_ThenUseOnlyItsPublicIdentityRoute()
    {
        var token = Token();
        var result = await Client(new FixtureHandler(request =>
        {
            Assert.Equal("/api/persons/527a1001-8ef5-4c9b-a565-222222222222", request.RequestUri!.AbsolutePath);
            Assert.Equal(token, request.Headers.Authorization?.Parameter);
            return Task.FromResult(Envelope(new { id = Identity, scopeId = CerberusOptionsValidatorTests.ValidOptions().HeimdallScopeId, role = 3, isDeleted = false }));
        })).RevalidateAsync(token, default);
        Assert.True(result);
    }

    [UnitFact]
    public async Task GivenChallengeCompletion_WhenCallingHeimdall_ThenSendProofOnlyInBody()
    {
        var token = Token();
        var result = await Client(new FixtureHandler(async request =>
        {
            Assert.Equal("/api/auth/2fa/verify", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("opaque-challenge", body.RootElement.GetProperty("challengeToken").GetString());
            Assert.Equal("123456", body.RootElement.GetProperty("code").GetString());
            return Envelope(new { token, expiresAt = DateTimeOffset.UtcNow.AddMinutes(5), emailVerified = true });
        })).CompleteChallengeAsync("opaque-challenge", "123456", null, default);
        Assert.Equal(token, result?.Token);
    }

    [UnitFact]
    public async Task GivenNarrowScopeOwnerCredential_WhenRegistering_ThenUseOnlyScopedUserCreation()
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        options.HeimdallServiceCredential = ServiceToken("2", options.HeimdallScopeId.ToString());
        var result = await Client(new FixtureHandler(async request =>
        {
            Assert.Equal("/api/scopes/527a1001-8ef5-4c9b-a565-111111111111/persons", request.RequestUri!.AbsolutePath);
            Assert.Equal(options.HeimdallServiceCredential, request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(body.RootElement.TryGetProperty("role", out _));
            Assert.Equal("fixture-password", body.RootElement.GetProperty("password").GetString());
            return Envelope(new { id = Identity, scopeId = options.HeimdallScopeId, role = 3, name = "Fixture", email = "fixture@example.test", emailVerified = false, createdAt = "2026-10-07T00:00:00Z" });
        }), options).RegisterAsync(new HeimdallRegistration("Fixture", "fixture@example.test", "fixture-password"), default);
        Assert.NotNull(result);
        Assert.Equal(Identity, result.Id);
        Assert.Equal(3, result.Role);
    }

    [UnitTheory]
    [InlineData("1")]
    [InlineData("3")]
    public async Task GivenUnprivilegedOrGlobalAdminCredential_WhenRegistering_ThenNeverUseAdminFallback(string role)
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        options.HeimdallServiceCredential = ServiceToken(role, options.HeimdallScopeId.ToString());
        var result = await Client(new FixtureHandler(_ => throw new InvalidOperationException("Disallowed credential reached transport")), options)
            .RegisterAsync(new HeimdallRegistration("Fixture", "fixture@example.test", "fixture-password"), default);
        Assert.Null(result);
    }

    private static string ServiceToken(string role, string ownedScopes)
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        return new JwtHandler().CreateToken(new JwtConfiguration(60, options.AuthIssuer!, options.AuthAudience!, options.AuthValidationSecret!,
            new Dictionary<string, string> { ["id"] = Identity.ToString(), ["roleId"] = role, ["ownedScopeIds"] = ownedScopes }));
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenConfiguredScopeOwner_WhenVerifyingRestoreAuthorization_ThenRequireCurrentOwnership(bool revoked)
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        options.HeimdallServiceCredential = ServiceToken("2", options.HeimdallScopeId.ToString());
        var result = await Client(new FixtureHandler(request =>
        {
            Assert.Equal(options.HeimdallServiceCredential, request.Headers.Authorization?.Parameter);
            if (request.RequestUri!.AbsolutePath == "/api/scopes/527a1001-8ef5-4c9b-a565-111111111111")
                return Task.FromResult(Envelope(new
                {
                    id = options.HeimdallScopeId,
                    name = "Fixture",
                    description = (string?)null,
                    googleSignInEnabled = false,
                    defaultLegalBasis = (int?)null,
                    privacyNoticeUri = (string?)null,
                    isDeleted = false,
                    ownerIds = revoked ? Array.Empty<Guid>() : new[] { Identity },
                    createdAt = "2026-10-07T00:00:00Z",
                    updatedAt = "2026-10-07T00:00:00Z"
                }));
            Assert.Equal("/api/persons/527a1001-8ef5-4c9b-a565-222222222222", request.RequestUri.AbsolutePath);
            return Task.FromResult(Envelope(new
            {
                id = Identity,
                name = "Fixture",
                email = "fixture@example.test",
                role = 2,
                emailVerified = true,
                twoFactorEnabled = false,
                isDeleted = false,
                scopeId = (Guid?)null,
                ownedScopeIds = new[] { options.HeimdallScopeId },
                createdAt = "2026-10-07T00:00:00Z",
                updatedAt = "2026-10-07T00:00:00Z"
            }));
        }), options).VerifyScopeAsync(default);
        Assert.Equal(!revoked, result);
    }

    private static string Token()
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        return new JwtHandler().CreateToken(new JwtConfiguration(60, options.AuthIssuer!, options.AuthAudience!, options.AuthValidationSecret!,
            new Dictionary<string, string> { ["id"] = Identity.ToString(), ["roleId"] = "3", ["scopeId"] = options.HeimdallScopeId.ToString() }));
    }

    [UnitFact]
    public async Task GivenHeadersButStalledBody_WhenReadingDependency_ThenApplyTimeoutToWholeResponse()
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) };
        response.Content.Headers.ContentType = new("application/json");
        using var transport = new HttpClient(new FixtureHandler(_ => Task.FromResult(response)))
        { BaseAddress = new Uri(options.HeimdallBaseUrl!), Timeout = TimeSpan.FromMilliseconds(50) };
        var client = new HeimdallClient(transport, options, new HeimdallTokenValidator(options));
        Assert.Null(await client.LoginAsync("fixture@example.test", "fixture-password", default).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private static HeimdallClient Client(HttpMessageHandler handler, ArturRios.Cerberus.Shared.Configuration.CerberusOptions? configuration = null)
    {
        var options = configuration ?? CerberusOptionsValidatorTests.ValidOptions();
        return new HeimdallClient(new HttpClient(handler) { BaseAddress = new Uri(options.HeimdallBaseUrl!) }, options, new HeimdallTokenValidator(options));
    }

    private static HttpResponseMessage Envelope(object data) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { success = true, errors = Array.Empty<string>(), messages = Array.Empty<string>(), timestamp = "2026-10-07T00:00:00Z", data }), Encoding.UTF8, "application/json") };

    private sealed class FixtureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
    }
}
