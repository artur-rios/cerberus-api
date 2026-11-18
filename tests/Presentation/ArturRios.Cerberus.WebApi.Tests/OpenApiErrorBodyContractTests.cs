using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArturRios.Cerberus.Query.Sync;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Cerberus.Shared.Operations;
using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Mediator.Command;
using ArturRios.Mediator.Query;
using ArturRios.Output;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace ArturRios.Cerberus.WebApi.Tests;

// Every documented error response describes the body the server writes for it, the way a generated client
// reads it: a handler's failed DataOutput ("data": null), the ProcessOutput of a request refused before its
// handler ran, the request limit's empty 413 and MVC's 415 ProblemDetails.
public sealed class OpenApiErrorBodyContractTests
{
    private static readonly string[] s_json = ["application/json", "text/json"];

    [UnitFact]
    public void GivenEveryEnvelopeAction_WhenItsHandlerFails_ThenEachDocumentedErrorAcceptsTheEnvelopeWithNullData()
    {
        using var host = OpenApiBodies.Host();
        var document = OpenApiBodies.Document(host);
        var serializer = host.Services.GetRequiredService<IOptions<MvcJsonOptions>>().Value.JsonSerializerOptions;
        var checkedResponses = 0;
        foreach (var (method, path, action) in Actions(host))
        {
            if (Envelope(action.MethodInfo.ReturnType) is not { } envelope)
                continue;
            // What ToActionResult writes for a failure: the action's own envelope, its data never set.
            var failure = (ProcessOutput)Activator.CreateInstance(envelope)!;
            failure.AddError("persistence_unavailable");
            var body = JsonSerializer.Serialize(failure, envelope, serializer);
            Assert.Contains("\"data\":null", body);
            foreach (var (status, response) in Errors(document, method, path))
            {
                checkedResponses++;
                foreach (var media in s_json)
                    Assert.Null(Reject(document, response, media, body, $"{method} {path} {status}"));
            }
        }
        Assert.True(checkedResponses > 300);
        // A failure envelope's data is null or absent, never a payload.
        Assert.NotNull(OpenApiBodies.Reject(document, document["components"]!["schemas"]!["FailureOutput"]!,
            "{\"messages\":[],\"errors\":[\"x\"],\"timestamp\":\"2026-11-17T00:00:00Z\",\"success\":false,\"data\":{}}"));
    }

    [UnitFact]
    public void GivenAnyOperation_WhenRefusedBeforeItsHandler_ThenEachDocumentedErrorAcceptsAProcessOutput()
    {
        using var host = OpenApiBodies.Host();
        var document = OpenApiBodies.Document(host);
        var serializer = host.Services.GetRequiredService<IOptions<MvcJsonOptions>>().Value.JsonSerializerOptions;
        // A controller's own check and InvalidModelStateResponseFactory answer an ObjectResult of ProcessOutput.
        var body = JsonSerializer.Serialize(ProcessOutput.New.WithError("validation_failed"), serializer);
        var checkedResponses = 0;
        foreach (var (method, path, status, response) in OpenApiBodies.Responses(document))
        {
            // /health/ready answers its own HealthStatus, and is neither authenticated nor behind the restore barrier.
            if (!Error(status, response) || path == "/health/ready")
                continue;
            checkedResponses++;
            Assert.Null(Reject(document, response, "application/json", body, $"{method} {path} {status}"));
        }
        Assert.True(checkedResponses > 300);
    }

    [UnitTheory]
    [InlineData("authentication")]
    [InlineData("restore")]
    [InlineData("vault-proof")]
    public async Task GivenRefusingMiddleware_WhenItAnswers_ThenEveryOperationDocumentsItsBodyForThatStatus(string middleware)
    {
        var document = OpenApiBodies.Document();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        RequestDelegate unreachable = _ => throw new InvalidOperationException("The middleware let the request through.");
        switch (middleware)
        {
            case "authentication":
                context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "fixture-protected-endpoint"));
                await new ProtectedEndpointMiddleware(unreachable).InvokeAsync(context, new HeimdallTokenValidator(Options()),
                    new Mock<IHeimdallClient>(MockBehavior.Strict).Object, new HeimdallApplicationTokenValidator(Options()));
                break;
            case "restore":
                await new RestoreBarrierMiddleware(unreachable).InvokeAsync(context, new RestoreTrafficGate());
                break;
            default:
                context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new VaultProofBodyAttribute()), "fixture-proof-endpoint"));
                context.Request.ContentType = "text/plain";
                await new VaultProofBodyMiddleware(unreachable).InvokeAsync(context);
                break;
        }
        var status = context.Response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.StartsWith("application/json", context.Response.ContentType);
        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.DoesNotContain("\"data\"", body);
        var documented = OpenApiBodies.Responses(document)
            .Where(r => r.Status == status && r.Path is not ("/health/live" or "/health/ready") && Error(r.Status, r.Response)).ToArray();
        Assert.NotEmpty(documented);
        foreach (var (method, path, _, response) in documented)
            Assert.Null(Reject(document, response, "application/json", body, $"{method} {path} {status}"));
    }

    [UnitFact]
    public async Task GivenRequestOverTheLimit_WhenAnyOperationIsCalled_ThenEveryOperationDocumentsA413WithoutBody()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentLength = 16;
        context.Response.Body = new MemoryStream();
        await new RequestLimitMiddleware(_ => throw new InvalidOperationException("The request was let through."), 8).InvokeAsync(context);
        Assert.Equal(413, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        var document = OpenApiBodies.Document();
        var operations = document["paths"]!.AsObject().SelectMany(path => path.Value!.AsObject().Select(operation => (path.Key, operation.Key, operation.Value!))).ToArray();
        Assert.Equal(64, operations.Length);
        foreach (var (path, method, operation) in operations)
        {
            var response = operation["responses"]!["413"];
            Assert.NotNull(response);
            Assert.Null(response["content"]);
        }
    }

    [UnitFact]
    public async Task GivenUnguardedBodyAction_WhenItsContentTypeIsUnreadable_ThenMvcAnswersTheDocumented415()
    {
        var document = OpenApiBodies.Document();
        await using var api = await Api();
        using var client = api.GetTestClient();

        using var refused = await client.PostAsync("/api/auth/login", new StringContent("{}", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, refused.StatusCode);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
        var response = document["paths"]!["/api/auth/login"]!["post"]!["responses"]!["415"]!.AsObject();
        Assert.Equal(["application/problem+json"], response["content"]!.AsObject().Select(media => media.Key));
        Assert.Null(Reject(document, response, "application/problem+json", await refused.Content.ReadAsStringAsync(), "POST /api/auth/login 415"));
        // Exactly the [FromBody] actions VaultProofBodyMiddleware does not guard: it answers the others' 400 itself.
        using var host = OpenApiBodies.Host();
        var unguarded = Actions(host)
            .Where(a => a.Action.Parameters.Any(p => p.BindingInfo?.BindingSource == BindingSource.Body)
                && a.Action.MethodInfo.GetCustomAttributes(typeof(VaultProofBodyAttribute), true).Length == 0)
            .Select(a => a.Method + " " + a.Path).Order().ToArray();
        Assert.Equal(7, unguarded.Length);
        Assert.Equal(unguarded, OpenApiBodies.Responses(document).Where(r => r.Status == "415").Select(r => r.Method + " " + r.Path).Order());
    }

    [UnitTheory]
    [InlineData("/api/auth/login", "application/json", "{\"email\":\"a@example.test\",\"password\":\"first\",\"password\":\"second\"}")]
    [InlineData("/api/auth/login", "application/json", "")]
    [InlineData("/api/auth/closure/login", "text/plain", "{}")]
    public async Task GivenUnbindableRequest_WhenPosted_ThenItsProcessOutput400IsTheDocumentedOne(string path, string media, string json)
    {
        var document = OpenApiBodies.Document();
        await using var api = await Api();
        using var client = api.GetTestClient();

        using var refused = await client.PostAsync(path, new StringContent(json, Encoding.UTF8, media));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.Contains("validation_failed", body);
        Assert.DoesNotContain("\"data\"", body);
        Assert.Null(Reject(document, document["paths"]![path]!["post"]!["responses"]!["400"]!.AsObject(), "application/json", body, "POST " + path + " 400"));
    }

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void GivenSyncConflict_WhenSerialized_ThenTheDocumented409AcceptsThePageAndTheNullData(bool resynchronization)
    {
        using var host = OpenApiBodies.Host();
        var document = OpenApiBodies.Document(host);
        var serializer = host.Services.GetRequiredService<IOptions<MvcJsonOptions>>().Value.JsonSerializerOptions;
        // resynchronization_required carries the page that clears the scope; revision_conflict carries none.
        var output = DataOutput<SyncChangesOutput?>.New;
        if (resynchronization)
        {
            output.WithData(new SyncChangesOutput
            {
                AccountId = Guid.NewGuid(),
                ProfileId = Guid.NewGuid(),
                PolicyRevision = 1,
                RevocationGeneration = 1,
                ServerTime = DateTimeOffset.UnixEpoch,
                ClearScope = true,
                ResynchronizationRequired = true
            });
        }
        output.WithError(resynchronization ? "resynchronization_required" : "revision_conflict");
        var body = JsonSerializer.Serialize(output, serializer);
        var response = document["paths"]!["/api/sync/changes"]!["get"]!["responses"]!["409"]!.AsObject();
        foreach (var media in s_json)
            Assert.Null(Reject(document, response, media, body, "GET /api/sync/changes 409"));
    }

    // The contract's controllers behind the middleware that sees a request before them, without the database,
    // the identity provider or the handlers: what is checked here is answered before any of them is used, or
    // (a handler nobody registered) by SafeFailureMiddleware's 500.
    internal static async Task<WebApplication> Api()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(Startup).Assembly.GetName().Name, Args = [] });
        builder.WebHost.UseTestServer();
        builder.Services.AddCerberusContracts();
        builder.Services.AddScoped<CommandMediator>();
        builder.Services.AddScoped<QueryMediator>();
        var api = builder.Build();
        api.UseMiddleware<SafeFailureMiddleware>();
        api.UseRouting();
        api.UseMiddleware<VaultProofBodyMiddleware>();
        api.MapControllers();
        await api.StartAsync();
        return api;
    }

    internal static IEnumerable<(string Method, string Path, ControllerActionDescriptor Action)> Actions(WebApplication host) =>
        host.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .Select(description => (description.HttpMethod!.ToUpperInvariant(), "/" + description.RelativePath, (ControllerActionDescriptor)description.ActionDescriptor));

    // The error responses a handler's failure can be: SafeFailureMiddleware's 500 never comes from one.
    private static IEnumerable<(string Status, JsonObject Response)> Errors(JsonNode document, string method, string path) =>
        OpenApiBodies.Responses(document).Where(r => r.Method == method && r.Path == path && r.Status != "500" && Error(r.Status, r.Response))
            .Select(r => (r.Status, r.Response));

    // An error response with a ProcessOutput-shaped body: every 4xx and 5xx but the empty 413 and MVC's 415.
    private static bool Error(string status, JsonObject response) =>
        status[0] is '4' or '5' && status is not ("413" or "415") && response["content"] is not null;

    // ActionResult<DataOutput<T>>'s DataOutput<T>.
    private static Type? Envelope(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            type = type.GetGenericArguments()[0];
        return type.IsGenericType && type.GetGenericArguments()[0] is { IsGenericType: true } output
            && output.GetGenericTypeDefinition() == typeof(DataOutput<>) ? output : null;
    }

    private static string? Reject(JsonNode document, JsonObject response, string media, string body, string where)
    {
        if (response["content"]?[media]?["schema"] is not { } schema)
            return where + ": no " + media + " body is documented";
        return OpenApiBodies.Reject(document, schema, body) is { } why ? where + " (" + media + "): " + why : null;
    }

    private static CerberusOptions Options() => new()
    {
        AuthIssuer = "fixture-issuer",
        AuthAudience = "fixture-audience",
        AuthValidationSecret = "fixture-only-signing-key-32-characters",
        HeimdallScopeId = Guid.Parse("527a1001-8ef5-4c9b-a565-111111111111")
    };
}
