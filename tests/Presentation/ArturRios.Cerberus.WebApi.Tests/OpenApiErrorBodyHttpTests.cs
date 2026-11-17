using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArturRios.Cerberus.WebApi.Tests;

// A real error body, read off the wire, against the response the published document gives for it.
[Collection("Registration host")]
public class OpenApiErrorBodyHttpTests(RegistrationApiFixture fixture, SharedApiHost api) : SharedApiTest(api)
{
    [FunctionalFact]
    public async Task GivenInputItsHandlerRefuses_WhenLoggingIn_ThenTheFailedEnvelopeWithNullDataIsTheDocumented400()
    {
        _ = Gateway.Client;
        var calls = fixture.IdentityCalls;

        using var response = await Gateway.Client.PostAsync("/api/auth/login",
            new StringContent("{\"email\":\"bad\",\"password\":\"fixture-password\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // The handler's own DataOutput: the member a ProcessOutput-only schema refused.
        Assert.Equal(JsonValueKind.Null, JsonNode.Parse(body)!["data"].GetValueKind());
        Assert.Null(Reject("/api/auth/login", "post", "400", response, body));
        Assert.Equal(calls, fixture.IdentityCalls);
    }

    [FunctionalFact]
    public async Task GivenNoCredentials_WhenReadingTheAccount_ThenTheMiddlewaresProcessOutputIsTheDocumented401()
    {
        using var response = await Gateway.Client.GetAsync("/api/accounts/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.False(JsonNode.Parse(body)!.AsObject().ContainsKey("data"));
        Assert.Null(Reject("/api/accounts/me", "get", "401", response, body));
    }

    private static string? Reject(string path, string method, string status, HttpResponseMessage response, string body)
    {
        var document = OpenApiBodies.Document();
        var media = response.Content.Headers.ContentType!.MediaType!;
        var schema = document["paths"]![path]![method]!["responses"]![status]!["content"]![media]!["schema"]!;
        return OpenApiBodies.Reject(document, schema, body);
    }
}
