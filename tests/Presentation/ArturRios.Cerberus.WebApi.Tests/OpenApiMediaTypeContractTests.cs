using System.Text;
using System.Text.Json.Nodes;
using ArturRios.Cerberus.WebApi.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Cerberus.WebApi.Tests;

// SafeFailureMiddleware's 500 on every operation, and only the media types the server writes: no text/plain,
// which MVC writes only for a string, and only application/json where middleware writes the body.
public sealed class OpenApiMediaTypeContractTests
{
    [UnitFact]
    public async Task GivenAnExceptionBehindSafeFailure_WhenItAnswers_ThenEveryOperationDocumentsItsJson500()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await new SafeFailureMiddleware(_ => throw new InvalidOperationException("fixture failure"), NullLogger<SafeFailureMiddleware>.Instance)
            .InvokeAsync(context);
        Assert.Equal(500, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);
        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Contains("internal_failure", body);

        var document = OpenApiBodies.Document();
        var operations = Operations(document).ToArray();
        Assert.Equal(64, operations.Length);
        foreach (var (_, _, operation) in operations)
        {
            var response = operation["responses"]!["500"];
            Assert.NotNull(response);
            // WriteAsJsonAsync's media type, whatever the request's Accept asked for.
            Assert.Equal(["application/json"], response["content"]!.AsObject().Select(media => media.Key));
            Assert.Null(OpenApiBodies.Reject(document, response["content"]!["application/json"]!["schema"]!, body));
            Assert.EndsWith("/ProcessOutput", response["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>());
        }
    }

    [UnitFact]
    public void GivenEveryResponse_WhenGeneratingOpenApi_ThenNoneListsTextPlain()
    {
        using var host = OpenApiBodies.Host();
        var document = OpenApiBodies.Document(host);
        // StringOutputFormatter writes text/plain only for a string, and no action answers one.
        var strings = host.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .SelectMany(description => description.SupportedResponseTypes.Where(type => type.Type == typeof(string))
                .Select(type => description.HttpMethod + " /" + description.RelativePath + " " + type.StatusCode))
            .ToArray();
        Assert.Empty(strings);
        var responses = OpenApiBodies.Responses(document).ToArray();
        Assert.Equal(544, responses.Length);
        var listed = responses.Where(r => r.Response["content"]?["text/plain"] is not null).Select(r => r.Method + " " + r.Path + " " + r.Status).ToArray();
        Assert.True(listed.Length == 0, "text/plain is listed on: " + string.Join(", ", listed));
    }

    [UnitFact]
    public void GivenResponsesOnlyMiddlewareWrites_WhenGeneratingOpenApi_ThenTheyListOnlyApplicationJson()
    {
        var document = OpenApiBodies.Document();
        // The 500 (SafeFailureMiddleware), and /health/details' 401 and 403 (ProtectedEndpointMiddleware's
        // operational authentication) are written with WriteAsJsonAsync; the 413 has no body at all.
        var responses = OpenApiBodies.Responses(document).ToArray();
        foreach (var (_, _, _, response) in responses.Where(r => r.Status == "500"
            || r.Path == "/health/details" && r.Status is "401" or "403"))
            Assert.Equal(["application/json"], response["content"]!.AsObject().Select(media => media.Key));
        Assert.All(responses.Where(r => r.Status == "413"), r => Assert.Null(r.Response["content"]));
        // An ObjectResult negotiates its JSON media type, so an envelope action's errors keep text/json.
        Assert.Equal(["application/json", "text/json"],
            document["paths"]!["/api/records/{id}"]!["get"]!["responses"]!["404"]!["content"]!.AsObject().Select(media => media.Key).Order());
    }

    private static IEnumerable<(string Method, string Path, JsonNode Operation)> Operations(JsonNode document) =>
        document["paths"]!.AsObject().SelectMany(path => path.Value!.AsObject()
            .Select(operation => (operation.Key.ToUpperInvariant(), path.Key, operation.Value!)));
}
