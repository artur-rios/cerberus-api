using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Jwt;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Query;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ArturRios.Cerberus.WebApi.Tests;

// What a real Kestrel and the API's own middleware answer for the requests no operation's handler sees, written
// as raw HTTP/1.1 on a socket so nothing between the client and Kestrel tidies them up, against what the
// document says about each.
public sealed class KestrelRefusalTests
{
    private static readonly CerberusOptions s_options = new()
    {
        AuthIssuer = "fixture-issuer",
        AuthAudience = "fixture-audience",
        AuthValidationSecret = "fixture-only-signing-key-32-characters",
        HeimdallScopeId = Guid.Parse("527a1001-8ef5-4c9b-a565-111111111111")
    };

    [UnitTheory]
    [InlineData("400", "GARBAGE\r\n\r\n")]
    [InlineData("400", "GET /health/live HTTP/1.1\r\n\r\n")]
    [InlineData("414", "GET /health/live?padding={0} HTTP/1.1\r\nHost: cerberus\r\n\r\n")]
    [InlineData("431", "GET /health/live HTTP/1.1\r\nHost: cerberus\r\nX-Padding: {0}{0}{0}{0}{0}\r\n\r\n")]
    [InlineData("505", "GET /health/live HTTP/1.2\r\nHost: cerberus\r\n\r\n")]
    [InlineData("408", "GET /health/live HTTP/1.1\r\nHost: cerberus\r\n")]
    public async Task GivenARequestKestrelRefuses_WhenSent_ThenItAnswersTheDocumentedBodilessStatus(string status, string request)
    {
        await using var api = await Api();

        var response = await Send(api, string.Format(System.Globalization.CultureInfo.InvariantCulture, request, new string('a', 9000)),
            wait: status == "408" ? TimeSpan.FromSeconds(4) : null);

        Assert.StartsWith("HTTP/1.1 " + status + " ", response);
        Assert.Contains("Content-Length: 0\r\n", response);
        // Every operation lists it, and says Kestrel answers it with no body.
        foreach (var (method, path, listed, description) in Responses(OpenApiBodies.Document(), status))
        {
            Assert.True(listed, method + " " + path + " does not list " + status);
            Assert.Contains("Kestrel", description);
            Assert.Contains("no body", description);
        }
    }

    [UnitTheory]
    [InlineData("/api/auth/login", "Transfer-Encoding: chunked\r\n\r\nZZ\r\n{}\r\n0\r\n\r\n", 0)]
    [InlineData("/api/auth/closure/login", "Transfer-Encoding: chunked\r\n\r\nZZ\r\n{}\r\n0\r\n\r\n", 0)]
    [InlineData("/api/auth/login", "Content-Length: 100\r\n\r\n{\"email\":", 4)]
    [InlineData("/api/auth/closure/login", "Content-Length: 100\r\n\r\n{\"email\":", 4)]
    public async Task GivenABodyKestrelRefusesAsItIsRead_WhenSent_ThenSafeFailureAnswersTheDocumented500(string path, string rest, int seconds)
    {
        await using var api = await Api();

        // Badly chunked, or slower than the minimum data rate: model binding and the vault-proof read alike.
        var response = await Send(api, "POST " + path + " HTTP/1.1\r\nHost: cerberus\r\nContent-Type: application/json\r\n" + rest,
            wait: seconds == 0 ? null : TimeSpan.FromSeconds(seconds));

        Assert.StartsWith("HTTP/1.1 500 ", response);
        Assert.Contains("Content-Type: application/json", response);
        var body = response[(response.IndexOf('{'))..(response.LastIndexOf('}') + 1)];
        Assert.Contains("internal_failure", body);
        var document = OpenApiBodies.Document();
        var documented = document["paths"]![path]!["post"]!["responses"]!["500"]!;
        Assert.Null(OpenApiBodies.Reject(document, documented["content"]!["application/json"]!["schema"]!, body));
        Assert.Contains("minimum data rate", documented["description"]!.GetValue<string>());
    }

    [UnitFact]
    public async Task GivenABodyCutShort_WhenTheClientStopsSending_ThenTheConnectionClosesWithNoResponse()
    {
        await using var api = await Api();

        var response = await Send(api, "POST /api/auth/login HTTP/1.1\r\nHost: cerberus\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n{\"email\":",
            shutdown: true);

        Assert.Equal("", response);
        Assert.Contains("short of its Content-Length", ContractRegistration.Description);
    }

    [UnitTheory]
    [InlineData("GET /api/nothing-here", false, "404")]
    [InlineData("PATCH /api/vault/protection", true, "405")]
    [InlineData("PATCH /api/vault/protection", false, "401")]
    [InlineData("HEAD /health/live", false, "401")]
    public async Task GivenARequestNoOperationTakes_WhenSent_ThenItIsAnsweredAsTheDescriptionSays(string line, bool authenticated, string status)
    {
        await using var api = await Api();
        var token = authenticated ? "Authorization: Bearer " + Token() + "\r\n" : "";

        var response = await Send(api, line + " HTTP/1.1\r\nHost: cerberus\r\nContent-Length: 0\r\n" + token + "Connection: close\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 " + status + " ", response);
        if (status == "401")
            Assert.Contains("Content-Type: application/json", response); // A HEAD response carries the headers only.
        else
            Assert.Contains("Content-Length: 0\r\n", response);
        Assert.Contains("answered " + (status == "401" ? "405" : status) + ", with no body", ContractRegistration.Description);
        Assert.Equal(ContractRegistration.Description, OpenApiBodies.Document()["info"]!["description"]!.GetValue<string>());
    }

    private static IEnumerable<(string Method, string Path, bool Listed, string Description)> Responses(JsonNode document, string status) =>
        document["paths"]!.AsObject().SelectMany(path => path.Value!.AsObject().Select(operation =>
        {
            var response = operation.Value!["responses"]![status];
            return (operation.Key, path.Key, response is not null, response?["description"]?.GetValue<string>() ?? "");
        }));

    private static string Token() => new JwtHandler().CreateToken(new JwtConfiguration(60, s_options.AuthIssuer!, s_options.AuthAudience!,
        s_options.AuthValidationSecret!, new Dictionary<string, string> { ["id"] = Guid.NewGuid().ToString(), ["scopeId"] = s_options.HeimdallScopeId.ToString() }));

    private static async Task<string> Send(WebApplication api, string request, bool shutdown = false, TimeSpan? wait = null)
    {
        var address = new Uri(api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, address.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        if (shutdown)
            client.Client.Shutdown(SocketShutdown.Send);
        if (wait is { } delay)
            await Task.Delay(delay);
        var buffer = new byte[4096];
        var read = new StringBuilder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                read.Append(Encoding.ASCII.GetString(buffer, 0, count));
        }
        catch (IOException)
        {
            // A reset after the response: what was read is the answer.
        }
        return read.ToString();
    }

    // The contract on a real Kestrel behind the production middleware that sees a request before a handler would,
    // in Startup's order, with the defaults Kestrel ships except two timeouts shortened so a test can wait them out.
    private static async Task<WebApplication> Api()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(Startup).Assembly.GetName().Name, Args = [] });
        builder.WebHost.UseKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0);
            kestrel.Limits.MinRequestBodyDataRate = new MinDataRate(10_000, TimeSpan.FromSeconds(2));
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(1);
        });
        builder.Services.AddCerberusContracts();
        builder.Services.AddScoped<CommandMediator>();
        builder.Services.AddScoped<QueryMediator>();
        builder.Services.AddSingleton(s_options);
        builder.Services.AddSingleton(new HeimdallTokenValidator(s_options));
        builder.Services.AddSingleton(new HeimdallApplicationTokenValidator(s_options));
        var identity = new Mock<IHeimdallClient>();
        identity.Setup(x => x.RevalidateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(HeimdallAuthorization.Authorized);
        builder.Services.AddSingleton(identity.Object);
        var api = builder.Build();
        api.UseMiddleware<NoStoreMiddleware>();
        api.UseMiddleware<SafeFailureMiddleware>();
        api.UseMiddleware<RequestLimitMiddleware>(1_000_000L);
        api.UseRouting();
        api.UseMiddleware<ProtectedEndpointMiddleware>();
        api.UseMiddleware<VaultProofBodyMiddleware>();
        api.MapControllers();
        await api.StartAsync();
        return api;
    }
}
