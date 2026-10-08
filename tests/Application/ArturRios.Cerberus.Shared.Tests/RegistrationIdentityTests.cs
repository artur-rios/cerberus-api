using System.Net;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Jwt;

namespace ArturRios.Cerberus.Shared.Tests;

public class RegistrationIdentityTests
{
    private static readonly Guid Identity = Guid.Parse("527a1001-8ef5-4c9b-a565-222222222222");
    private static readonly HeimdallRegistration Input = new("Fixture Owner", "fixture@example.test", "fixture-password");

    [UnitFact]
    public async Task GivenNewIdentity_WhenRegistering_ThenCreateOnlyInConfiguredScope()
    {
        var options = Options();
        var calls = new List<string>();
        using var transport = Transport(request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            if (calls.Count == 1) return Status(401);
            Assert.Equal(options.HeimdallServiceCredential, request.Headers.Authorization?.Parameter);
            return Person(options);
        }, options);
        var result = await Client(transport, options).EstablishRegistrationIdentityAsync(Input, null, default);
        Assert.Equal(RegistrationIdentityStatus.Verified, result.Status);
        Assert.Equal(Identity, result.IdentityId);
        Assert.Equal(new[] { "/api/auth/login", $"/api/scopes/{options.HeimdallScopeId:D}/persons" }, calls);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenExistingIdentityProof_WhenRegistering_ThenRevalidateWithoutCreating(bool explicitProof)
    {
        var options = Options();
        using var transport = Transport(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/auth/login" when !explicitProof => Login(options),
            "/api/persons/527a1001-8ef5-4c9b-a565-222222222222" => Person(options),
            _ => throw new InvalidOperationException("An existing proved identity must not be created again.")
        }, options);
        var result = await Client(transport, options).EstablishRegistrationIdentityAsync(Input, explicitProof ? Token(options) : null, default);
        Assert.Equal(RegistrationIdentityStatus.Verified, result.Status);
        Assert.Equal(Identity, result.IdentityId);
    }

    [UnitTheory]
    [InlineData(503)]
    [InlineData(200)]
    public async Task GivenUnavailableOrMalformedLogin_WhenRegistering_ThenDoNotCreate(int status)
    {
        var options = Options();
        using var transport = Transport(request =>
        {
            Assert.Equal("/api/auth/login", request.RequestUri!.AbsolutePath);
            return Status(status);
        }, options);
        var result = await Client(transport, options).EstablishRegistrationIdentityAsync(Input, null, default);
        Assert.Equal(RegistrationIdentityStatus.Unavailable, result.Status);
        Assert.Null(result.IdentityId);
    }

    [UnitFact]
    public async Task GivenPendingMfa_WhenRegistering_ThenRequireCompletedIdentityProof()
    {
        var options = Options();
        using var transport = Transport(request =>
        {
            Assert.Equal("/api/auth/login", request.RequestUri!.AbsolutePath);
            return Envelope(new { requiresTwoFactor = true, challengeToken = "fixture-challenge", availableMethods = new[] { "App" } });
        }, options);
        var result = await Client(transport, options).EstablishRegistrationIdentityAsync(Input, null, default);
        Assert.Equal(RegistrationIdentityStatus.Denied, result.Status);
        Assert.Null(result.IdentityId);
    }

    [UnitTheory]
    [InlineData(409, RegistrationIdentityStatus.Denied)]
    [InlineData(401, RegistrationIdentityStatus.Forbidden)]
    [InlineData(403, RegistrationIdentityStatus.Forbidden)]
    [InlineData(404, RegistrationIdentityStatus.NotFound)]
    [InlineData(503, RegistrationIdentityStatus.Unavailable)]
    public async Task GivenCreationRejection_WhenRegistering_ThenDoNotInferIdentityFromEmail(int status, RegistrationIdentityStatus expected)
    {
        var options = Options();
        using var transport = Transport(request => request.RequestUri!.AbsolutePath == "/api/auth/login" ? Status(401) : Status(status), options);
        var result = await Client(transport, options).EstablishRegistrationIdentityAsync(Input, null, default);
        Assert.Equal(expected, result.Status);
        Assert.Null(result.IdentityId);
    }

    [UnitTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task GivenInvalidCreationIdentity_WhenRegistering_ThenReject(int invalid)
    {
        var options = Options();
        using var transport = Transport(request => request.RequestUri!.AbsolutePath == "/api/auth/login" ? Status(401) :
            Envelope(new { id = Identity, scopeId = invalid == 0 ? Guid.NewGuid() : options.HeimdallScopeId, role = invalid == 1 ? 2 : 3, isDeleted = invalid == 2 }), options);
        Assert.Equal(RegistrationIdentityStatus.Unavailable,
            (await Client(transport, options).EstablishRegistrationIdentityAsync(Input, null, default)).Status);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenInvalidServiceOrIdentityProof_WhenRegistering_ThenDenyWithoutFallback(bool invalidService)
    {
        var options = Options();
        if (invalidService) options.HeimdallServiceCredential = "invalid-service-proof";
        using var transport = Transport(_ => throw new InvalidOperationException("No fallback may send credentials."), options);
        var result = await Client(transport, options).EstablishRegistrationIdentityAsync(Input, invalidService ? null : "invalid-user-proof", default);
        Assert.Equal(invalidService ? RegistrationIdentityStatus.Forbidden : RegistrationIdentityStatus.Denied, result.Status);
    }

    [UnitFact]
    public async Task GivenLostCreationResponse_WhenRetrying_ThenProveExistingIdentityWithoutSecondCreation()
    {
        var options = Options();
        var created = false;
        var creations = 0;
        using var transport = Transport(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/auth/login") return created ? Login(options) : Status(401);
            if (path == $"/api/persons/{Identity:D}") return Person(options);
            creations++;
            created = true;
            throw new TaskCanceledException("Simulate response lost after upstream commit.");
        }, options);
        var client = Client(transport, options);
        Assert.Equal(RegistrationIdentityStatus.Unavailable, (await client.EstablishRegistrationIdentityAsync(Input, null, default)).Status);
        var retry = await client.EstablishRegistrationIdentityAsync(Input, null, default);
        Assert.Equal(RegistrationIdentityStatus.Verified, retry.Status);
        Assert.Equal(Identity, retry.IdentityId);
        Assert.Equal(1, creations);
    }

    private static CerberusOptions Options()
    {
        var options = CerberusOptionsValidatorTests.ValidOptions();
        options.HeimdallServiceCredential = Token(options, service: true);
        return options;
    }
    private static string Token(CerberusOptions options, bool service = false) => new JwtHandler().CreateToken(
        new JwtConfiguration(60, options.AuthIssuer!, options.AuthAudience!, options.AuthValidationSecret!,
            service ? new Dictionary<string, string> { ["id"] = Identity.ToString(), ["roleId"] = "2", ["ownedScopeIds"] = options.HeimdallScopeId.ToString() }
            : new Dictionary<string, string> { ["id"] = Identity.ToString(), ["roleId"] = "3", ["scopeId"] = options.HeimdallScopeId.ToString() }));
    private static HttpResponseMessage Login(CerberusOptions options) => Envelope(new { token = Token(options), expiresAt = DateTimeOffset.UtcNow.AddMinutes(5), emailVerified = true, requiresTwoFactor = false });
    private static HttpResponseMessage Person(CerberusOptions options) => Envelope(new { id = Identity, scopeId = options.HeimdallScopeId, role = 3, isDeleted = false });
    private static HttpResponseMessage Envelope(object data) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { success = true, errors = Array.Empty<string>(), data }), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Status(int status) => new((HttpStatusCode)status)
    { Content = new StringContent("upstream-sensitive-body", Encoding.UTF8, "application/json") };
    private static HttpClient Transport(Func<HttpRequestMessage, HttpResponseMessage> response, CerberusOptions options) =>
        new(new Handler(response)) { BaseAddress = new Uri(options.HeimdallBaseUrl!) };
    private static HeimdallClient Client(HttpClient transport, CerberusOptions options) => new(transport, options, new HeimdallTokenValidator(options));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
