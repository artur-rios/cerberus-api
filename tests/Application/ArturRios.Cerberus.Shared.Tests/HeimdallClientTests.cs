using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Jwt;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ArturRios.Cerberus.Shared.Tests;

public sealed class HeimdallClientTests
{
    private static readonly Guid Identity = Guid.Parse("527a1001-8ef5-4c9b-a565-222222222222");
    private static readonly Guid Application = Guid.Parse("527a1001-8ef5-4c9b-a565-333333333333");

    [UnitFact]
    public async Task GivenActiveScopedApplication_WhenVerifying_ThenUsePublicRouteAndServiceCredential()
    {
        var applicationId = Guid.Parse("527a1001-8ef5-4c9b-a565-333333333333");
        var options = CerberusOptionsValidatorTests.ValidOptions();
        options.HeimdallServiceCredential = ServiceToken("2", options.HeimdallScopeId.ToString());
        using var cancellation = new CancellationTokenSource();
        var handler = new FixtureHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/scopes/527a1001-8ef5-4c9b-a565-111111111111/applications/527a1001-8ef5-4c9b-a565-333333333333", request.RequestUri!.PathAndQuery);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(options.HeimdallServiceCredential, request.Headers.Authorization.Parameter);
            Assert.Null(request.Content);
            return Task.FromResult(Envelope(new
            {
                id = applicationId,
                name = "Fixture application",
                scopeId = options.HeimdallScopeId,
                ownerId = Identity,
                isDeleted = false,
                createdAt = "2026-10-07T00:00:00Z",
                updatedAt = "2026-10-07T00:00:00Z"
            }));
        });

        var status = await Client(handler, options).VerifyApplicationAsync(applicationId, cancellation.Token);

        Assert.Equal(HeimdallApplicationStatus.Active, status);
    }

    [UnitTheory]
    [InlineData("wrong-id", HeimdallApplicationStatus.Unavailable)]
    [InlineData("wrong-scope", HeimdallApplicationStatus.Unavailable)]
    [InlineData("deleted", HeimdallApplicationStatus.NotFound)]
    [InlineData("deleted-wrong-id", HeimdallApplicationStatus.Unavailable)]
    [InlineData("deleted-wrong-scope", HeimdallApplicationStatus.Unavailable)]
    [InlineData("unauthorized", HeimdallApplicationStatus.Unavailable)]
    [InlineData("forbidden", HeimdallApplicationStatus.Unavailable)]
    [InlineData("not-found", HeimdallApplicationStatus.NotFound)]
    [InlineData("server-error", HeimdallApplicationStatus.Unavailable)]
    public async Task GivenApplicationResponse_WhenVerifying_ThenClassifyFailClosed(string kind, HeimdallApplicationStatus expected)
    {
        var options = ApplicationOptions();
        var data = ApplicationData();
        if (kind is "wrong-id" or "deleted-wrong-id") data["id"] = Identity;
        if (kind is "wrong-scope" or "deleted-wrong-scope") data["scopeId"] = Identity;
        if (kind.StartsWith("deleted", StringComparison.Ordinal)) data["isDeleted"] = true;
        var response = kind switch
        {
            "unauthorized" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            "forbidden" => new HttpResponseMessage(HttpStatusCode.Forbidden),
            "not-found" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "server-error" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => Envelope(data)
        };

        var status = await Client(new FixtureHandler(_ => Task.FromResult(response)), options).VerifyApplicationAsync(Application, default);

        Assert.Equal(expected, status);
    }

    [UnitTheory]
    [InlineData("application")]
    [InlineData("scope")]
    public async Task GivenEmptyApplicationOrScopeId_WhenVerifying_ThenFailWithoutTransport(string empty)
    {
        var options = ApplicationOptions();
        var applicationId = Application;
        if (empty == "application") applicationId = Guid.Empty;
        else
        {
            options.HeimdallScopeId = Guid.Empty;
            options.HeimdallServiceCredential = ServiceToken("2", Guid.Empty.ToString());
        }
        var handler = new FixtureHandler(_ => throw new InvalidOperationException("Invalid configuration reached transport"));

        var status = await Client(handler, options).VerifyApplicationAsync(applicationId, default);

        Assert.Equal(HeimdallApplicationStatus.Unavailable, status);
    }

    [UnitTheory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("global-admin")]
    [InlineData("user")]
    [InlineData("wrong-scope")]
    [InlineData("multiple-scopes")]
    public async Task GivenInvalidServiceCredential_WhenVerifyingApplication_ThenFailWithoutTransport(string kind)
    {
        var options = ApplicationOptions();
        options.HeimdallServiceCredential = kind switch
        {
            "missing" => null,
            "empty" => string.Empty,
            "malformed" => "invalid-service-token",
            "global-admin" => ServiceToken("1", options.HeimdallScopeId.ToString()),
            "user" => ServiceToken("3", options.HeimdallScopeId.ToString()),
            "wrong-scope" => ServiceToken("2", Identity.ToString()),
            _ => ServiceToken("2", $"{options.HeimdallScopeId},{Identity}")
        };
        var handler = new FixtureHandler(_ => throw new InvalidOperationException("Disallowed credential reached transport"));

        var status = await Client(handler, options).VerifyApplicationAsync(Application, default);

        Assert.Equal(HeimdallApplicationStatus.Unavailable, status);
    }

    [UnitTheory]
    [InlineData("id", false)]
    [InlineData("name", false)]
    [InlineData("scopeId", false)]
    [InlineData("ownerId", false)]
    [InlineData("isDeleted", false)]
    [InlineData("createdAt", false)]
    [InlineData("updatedAt", false)]
    [InlineData("id", true)]
    [InlineData("name", true)]
    [InlineData("scopeId", true)]
    [InlineData("ownerId", true)]
    [InlineData("isDeleted", true)]
    [InlineData("createdAt", true)]
    [InlineData("updatedAt", true)]
    public async Task GivenMissingOrCaseForgedApplicationField_WhenVerifying_ThenFailClosed(string field, bool forged)
    {
        var data = ApplicationData();
        var value = data[field];
        data.Remove(field);
        if (forged) data[char.ToUpperInvariant(field[0]) + field[1..]] = value;

        var status = await Client(new FixtureHandler(_ => Task.FromResult(Envelope(data))), ApplicationOptions())
            .VerifyApplicationAsync(Application, default);

        Assert.Equal(HeimdallApplicationStatus.Unavailable, status);
    }

    [UnitTheory]
    [InlineData("id", "not-a-uuid")]
    [InlineData("scopeId", "not-a-uuid")]
    [InlineData("ownerId", "not-a-uuid")]
    [InlineData("isDeleted", "false")]
    [InlineData("createdAt", "not-a-date")]
    [InlineData("updatedAt", "not-a-date")]
    [InlineData("name", null)]
    [InlineData("unknown", "unexpected")]
    public async Task GivenInvalidApplicationField_WhenVerifying_ThenFailClosed(string field, string? value)
    {
        var data = ApplicationData();
        data[field] = value;

        var status = await Client(new FixtureHandler(_ => Task.FromResult(Envelope(data))), ApplicationOptions())
            .VerifyApplicationAsync(Application, default);

        Assert.Equal(HeimdallApplicationStatus.Unavailable, status);
    }

    [UnitTheory]
    [InlineData("duplicate-data")]
    [InlineData("duplicate-envelope")]
    [InlineData("not-json")]
    [InlineData("rejected-envelope")]
    [InlineData("errors-envelope")]
    [InlineData("wrong-media")]
    [InlineData("oversized")]
    [InlineData("oversized-stream")]
    public async Task GivenMalformedApplicationResponse_WhenVerifying_ThenFailClosed(string kind)
    {
        var options = ApplicationOptions();
        var json = JsonSerializer.Serialize(ApplicationData());
        if (kind == "duplicate-data") json = json.Replace("\"isDeleted\":false", "\"isDeleted\":true,\"isDeleted\":false", StringComparison.Ordinal);
        var body = $"{{\"success\":true,\"errors\":[],\"data\":{json}}}";
        body = kind switch
        {
            "duplicate-envelope" => body.Replace("\"success\":true", "\"success\":false,\"success\":true", StringComparison.Ordinal),
            "not-json" => "upstream-secret",
            "rejected-envelope" => body.Replace("\"success\":true", "\"success\":false", StringComparison.Ordinal),
            "errors-envelope" => body.Replace("\"errors\":[]", "\"errors\":[\"upstream-secret\"]", StringComparison.Ordinal),
            _ => body
        };
        if (kind.StartsWith("oversized", StringComparison.Ordinal)) options.MaxRequestBytes = Encoding.UTF8.GetByteCount(body) - 1;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = kind == "oversized-stream" ? new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(body))) : new StringContent(body, Encoding.UTF8) };
        response.Content.Headers.ContentType = new(kind == "wrong-media" ? "text/plain" : "application/json");
        if (kind == "oversized-stream") Assert.Null(response.Content.Headers.ContentLength);

        var status = await Client(new FixtureHandler(_ => Task.FromResult(response)), options).VerifyApplicationAsync(Application, default);

        Assert.Equal(HeimdallApplicationStatus.Unavailable, status);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenApplicationDependencyFailure_WhenVerifying_ThenReturnUnavailable(bool timeout)
    {
        var handler = new FixtureHandler(_ => timeout ? throw new TaskCanceledException("upstream-secret") : throw new HttpRequestException("upstream-secret"));

        var status = await Client(handler, ApplicationOptions()).VerifyApplicationAsync(Application, default);

        Assert.Equal(HeimdallApplicationStatus.Unavailable, status);
    }

    [UnitFact]
    public async Task GivenStalledApplicationBody_WhenVerifying_ThenBoundWholeResponseTime()
    {
        var options = ApplicationOptions();
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) };
        response.Content.Headers.ContentType = new("application/json");
        using var transport = new HttpClient(new FixtureHandler(_ => Task.FromResult(response)))
        { BaseAddress = new Uri(options.HeimdallBaseUrl!), Timeout = TimeSpan.FromMilliseconds(50) };
        var client = new HeimdallClient(transport, options, new HeimdallTokenValidator(options));

        var status = await client.VerifyApplicationAsync(Application, default).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HeimdallApplicationStatus.Unavailable, status);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenCanceledApplicationVerification_WhenVerifying_ThenPropagateCancellation(bool invalidCredential)
    {
        var options = ApplicationOptions();
        if (invalidCredential) options.HeimdallServiceCredential = null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new FixtureHandler(_ => throw new InvalidOperationException("Canceled request reached transport"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler, options).VerifyApplicationAsync(Application, cancellation.Token));
    }

    [UnitFact]
    public async Task GivenCallerCancellationDuringApplicationTransport_WhenVerifying_ThenPropagateCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new FixtureHandler(_ =>
        {
            cancellation.Cancel();
            throw new TaskCanceledException("Caller canceled");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler, ApplicationOptions()).VerifyApplicationAsync(Application, cancellation.Token));
    }

    private static ArturRios.Cerberus.Shared.Configuration.CerberusOptions ApplicationOptions()
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        options.HeimdallServiceCredential = ServiceToken("2", options.HeimdallScopeId.ToString());
        return options;
    }

    private static Dictionary<string, object?> ApplicationData() => new()
    {
        ["id"] = Application,
        ["name"] = "Fixture application",
        ["scopeId"] = Guid.Parse("527a1001-8ef5-4c9b-a565-111111111111"),
        ["ownerId"] = Identity,
        ["isDeleted"] = false,
        ["createdAt"] = "2026-10-07T00:00:00Z",
        ["updatedAt"] = "2026-10-07T00:00:00Z"
    };

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

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
        Assert.Equal(HeimdallAuthorization.Denied, result);
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
        Assert.Equal(HeimdallAuthorization.Authorized, result);
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
