using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Operations;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Jwt;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Net;
using System.Text;

namespace ArturRios.Cerberus.WebApi.Tests;

public sealed class SecurityBarrierTests
{
    [UnitFact]
    public async Task GivenProtectedEndpointWithoutCredentials_WhenRequested_ThenNeverExecuteEndpoint()
    {
        var context = Context();
        var called = false;
        await new ProtectedEndpointMiddleware(_ => { called = true; return Task.CompletedTask; })
            .InvokeAsync(context, new HeimdallTokenValidator(Options()), new Mock<IHeimdallClient>(MockBehavior.Strict).Object);
        Assert.False(called);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenValidToken_WhenRevalidatingCurrentIdentity_ThenAuthorizeOnlyIfStillActive(bool active)
    {
        var context = Context();
        var options = Options();
        var token = new JwtHandler().CreateToken(new JwtConfiguration(60, options.AuthIssuer!, options.AuthAudience!, options.AuthValidationSecret!,
            new Dictionary<string, string> { ["id"] = "527a1001-8ef5-4c9b-a565-222222222222", ["scopeId"] = options.HeimdallScopeId.ToString() }));
        context.Request.Headers.Authorization = "Bearer " + token;
        var upstream = new Mock<IHeimdallClient>(MockBehavior.Strict);
        upstream.Setup(x => x.RevalidateAsync(token, It.IsAny<CancellationToken>()))
            .ReturnsAsync(active ? HeimdallAuthorization.Authorized : HeimdallAuthorization.Denied);
        var called = false;
        await new ProtectedEndpointMiddleware(_ => { called = true; return Task.CompletedTask; })
            .InvokeAsync(context, new HeimdallTokenValidator(options), upstream.Object);
        Assert.Equal(active, called);
        if (active) Assert.Equal("527a1001-8ef5-4c9b-a565-222222222222", context.User.FindFirst("id")?.Value);
        else Assert.Equal(401, context.Response.StatusCode);
    }

    [UnitFact]
    public async Task GivenIncompleteRestore_WhenRequesting_ThenKeepTrafficDisabled()
    {
        var context = Context();
        var called = false;
        await new RestoreBarrierMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context, new RestoreTrafficGate());
        Assert.False(called);
        Assert.Equal(503, context.Response.StatusCode);
    }

    [UnitTheory]
    [InlineData(503, 503, "identity_unavailable")]
    [InlineData(200, 503, "identity_unavailable")]
    [InlineData(401, 401, "authentication_required")]
    [InlineData(404, 401, "authentication_required")]
    public async Task GivenValidIdentityAndUpstreamFailure_WhenRequestingProtectedEndpoint_ThenDistinguishDenialFromOutage(int upstreamStatus, int expected, string code)
    {
        var options = Options();
        options.MaxRequestBytes = 1024;
        var token = new JwtHandler().CreateToken(new JwtConfiguration(60, options.AuthIssuer!, options.AuthAudience!, options.AuthValidationSecret!,
            new Dictionary<string, string> { ["id"] = Guid.NewGuid().ToString(), ["scopeId"] = options.HeimdallScopeId.ToString() }));
        using var transport = new HttpClient(new DependencyFailure((HttpStatusCode)upstreamStatus)) { BaseAddress = new Uri("https://fixture.example.test") };
        var identity = new HeimdallClient(transport, options, new HeimdallTokenValidator(options));
        var context = Context();
        context.Request.Headers.Authorization = "Bearer " + token;
        var executed = false;
        await new ProtectedEndpointMiddleware(_ => { executed = true; return Task.CompletedTask; })
            .InvokeAsync(context, new HeimdallTokenValidator(options), identity);
        Assert.False(executed);
        Assert.Equal(expected, context.Response.StatusCode);
        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Contains(code, body);
        Assert.DoesNotContain("upstream-protected", body);
    }

    private sealed class DependencyFailure(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("upstream-protected", Encoding.UTF8, "application/json") });
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "fixture-protected-endpoint"));
        return context;
    }

    private static CerberusOptions Options() => new()
    {
        AuthIssuer = "fixture-issuer",
        AuthAudience = "fixture-audience",
        AuthValidationSecret = "fixture-only-signing-key-32-characters",
        HeimdallScopeId = Guid.Parse("527a1001-8ef5-4c9b-a565-111111111111")
    };
}
