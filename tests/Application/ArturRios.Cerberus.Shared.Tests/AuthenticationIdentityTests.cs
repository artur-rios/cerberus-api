using System.Net;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Jwt;

namespace ArturRios.Cerberus.Shared.Tests;

public class AuthenticationIdentityTests
{
    private static readonly Guid Identity = Guid.Parse("527a1001-8ef5-4c9b-a565-222222222222");

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenCompletedScopedAuthentication_WhenAuthenticating_ThenRevalidateBeforeReturningIdentity(bool challenge)
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        var calls = new List<string>();
        var expectedToken = Token(options);
        using var transport = Transport(request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            if (calls.Count == 1)
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Null(request.Headers.Authorization);
                if (challenge)
                {
                    Assert.Equal("opaque-challenge", body.RootElement.GetProperty("challengeToken").GetString());
                    Assert.Equal("fixture-recovery", body.RootElement.GetProperty("recoveryCode").GetString());
                }
                else Assert.Equal(options.HeimdallScopeId, body.RootElement.GetProperty("scopeId").GetGuid());
                return Login(options, challenge, expectedToken);
            }
            Assert.Equal(expectedToken, request.Headers.Authorization?.Parameter);
            return Person(options);
        }, options);
        var result = await Authenticate(Client(transport, options), challenge);
        Assert.Null(result.Error);
        Assert.Equal(Identity, result.IdentityId);
        Assert.Equal(expectedToken, result.Login!.Token);
        Assert.False(result.Login.RequiresTwoFactor);
        Assert.Equal(new[] { challenge ? "/api/auth/2fa/verify" : "/api/auth/login", $"/api/persons/{Identity:D}" }, calls);
    }

    [UnitFact]
    public async Task GivenPendingMfa_WhenAuthenticating_ThenReturnChallengeWithoutCurrentIdentityLookup()
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        using var transport = Transport(request =>
        {
            Assert.Equal("/api/auth/login", request.RequestUri!.AbsolutePath);
            return Envelope(new { requiresTwoFactor = true, challengeToken = "opaque-challenge", availableMethods = new[] { "App" } });
        }, options);
        var result = await Authenticate(Client(transport, options), false);
        Assert.Null(result.Error);
        Assert.Null(result.IdentityId);
        Assert.True(result.Login!.RequiresTwoFactor);
        Assert.Null(result.Login.Token);
    }

    [UnitTheory]
    [InlineData(false, 401, "authentication_required")]
    [InlineData(false, 403, "authentication_forbidden")]
    [InlineData(false, 404, "authentication_required")]
    [InlineData(false, 503, "identity_unavailable")]
    [InlineData(false, 200, "identity_unavailable")]
    [InlineData(true, 401, "authentication_required")]
    [InlineData(true, 403, "authentication_forbidden")]
    [InlineData(true, 503, "identity_unavailable")]
    public async Task GivenDeniedOrMalformedDependency_WhenAuthenticating_ThenReturnStableTypedError(bool challenge, int status, string expected)
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        using var transport = Transport(_ => Status(status), options);
        var result = await Authenticate(Client(transport, options), challenge);
        Assert.Equal(expected, result.Error);
        Assert.Null(result.Login);
        Assert.Null(result.IdentityId);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenWrongScopeOrInvalidBearer_WhenAuthenticating_ThenRejectWithoutCurrentLookup(bool wrongScope)
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        using var transport = Transport(request =>
        {
            Assert.Equal("/api/auth/login", request.RequestUri!.AbsolutePath);
            return Envelope(new { requiresTwoFactor = false, token = wrongScope ? Token(options, Guid.NewGuid()) : "invalid-token", expiresAt = DateTimeOffset.UtcNow.AddMinutes(5), emailVerified = true });
        }, options);
        var result = await Authenticate(Client(transport, options), false);
        Assert.Equal("authentication_required", result.Error);
        Assert.Null(result.Login);
    }

    [UnitTheory]
    [InlineData(false, 401, "authentication_required")]
    [InlineData(true, 401, "authentication_required")]
    [InlineData(false, 503, "identity_unavailable")]
    [InlineData(true, 503, "identity_unavailable")]
    [InlineData(false, 200, "authentication_required")]
    public async Task GivenCurrentIdentityDeniedOrUnavailable_WhenAuthenticating_ThenReturnNoBearer(bool challenge, int status, string expected)
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        using var transport = Transport(request => request.Method == HttpMethod.Post ? Login(options, challenge)
            : status == 200 ? Person(options, deleted: true) : Status(status), options);
        var result = await Authenticate(Client(transport, options), challenge);
        Assert.Equal(expected, result.Error);
        Assert.Null(result.Login);
        Assert.Null(result.IdentityId);
    }

    [UnitFact]
    public async Task GivenPendingResultForChallengeCompletion_WhenVerifying_ThenRejectIncompatibleResponse()
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        using var transport = Transport(_ => Envelope(new { requiresTwoFactor = true, challengeToken = "again", availableMethods = new[] { "App" } }), options);
        Assert.Equal("identity_unavailable", (await Authenticate(Client(transport, options), true)).Error);
    }

    [UnitFact]
    public async Task GivenCancelledCaller_WhenAuthenticating_ThenPropagateCancellationWithoutRequest()
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        using var transport = Transport(_ => throw new InvalidOperationException("Must not call dependency."), options);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(transport, options).AuthenticateAsync("fixture@example.test", "fixture-password", cancelled.Token));
    }

    private static Task<HeimdallAuthentication> Authenticate(HeimdallClient client, bool challenge) => challenge
        ? client.VerifyChallengeAsync("opaque-challenge", null, "fixture-recovery", default)
        : client.AuthenticateAsync("fixture@example.test", "fixture-password", default);
    private static string Token(CerberusOptions options, Guid? scope = null) => new JwtHandler().CreateToken(new JwtConfiguration(60, options.AuthIssuer!, options.AuthAudience!, options.AuthValidationSecret!,
        new Dictionary<string, string> { ["id"] = Identity.ToString(), ["roleId"] = "3", ["scopeId"] = (scope ?? options.HeimdallScopeId).ToString() }));
    private static HttpResponseMessage Login(CerberusOptions options, bool completion, string? bearer = null) => completion
        ? Envelope(new { token = bearer ?? Token(options), expiresAt = DateTimeOffset.UtcNow.AddMinutes(5), emailVerified = true })
        : Envelope(new { token = bearer ?? Token(options), expiresAt = DateTimeOffset.UtcNow.AddMinutes(5), emailVerified = true, requiresTwoFactor = false });
    private static HttpResponseMessage Person(CerberusOptions options, bool deleted = false) => Envelope(new { id = Identity, scopeId = options.HeimdallScopeId, role = 3, isDeleted = deleted });
    private static HttpResponseMessage Envelope(object data) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { success = true, errors = Array.Empty<string>(), data }), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Status(int status) => new((HttpStatusCode)status)
    { Content = new StringContent("upstream-sensitive-body", Encoding.UTF8, "application/json") };
    private static HttpClient Transport(Func<HttpRequestMessage, HttpResponseMessage> response, CerberusOptions options) => new(new Handler(response)) { BaseAddress = new Uri(options.HeimdallBaseUrl!) };
    private static HeimdallClient Client(HttpClient transport, CerberusOptions options) => new(transport, options, new HeimdallTokenValidator(options));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
