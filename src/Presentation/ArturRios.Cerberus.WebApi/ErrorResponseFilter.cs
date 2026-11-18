using ArturRios.Cerberus.WebApi.Middleware;
using ArturRios.Output;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ArturRios.Cerberus.WebApi;

// Documentation only: every error response describes the body the server writes for it.
//
// - A handler's failure goes through ToActionResult and its status map, so it is the action's whole
//   DataOutput envelope with "data": null. A request refused before its handler ran (a controller's own
//   check, model binding through InvalidModelStateResponseFactory, the authentication, restore-barrier
//   and vault-proof middleware) is a ProcessOutput, with no data member. An action answering a
//   DataOutput documents its errors as FailureOutput, which is both: ProcessOutput's members and an
//   optional data that is only ever null. (GET /api/sync/changes' 409 resynchronization_required also
//   carries its page: that envelope or FailureOutput.) The account export and the health routes write
//   ProcessOutput bodies only, and keep it.
// - 413 is RequestLimitMiddleware's, and has no body. It answers any request whose Content-Length is over
//   the limit, so every operation lists it.
// - A [FromBody] action that VaultProofBodyMiddleware does not guard leaves an unreadable Content-Type to
//   MVC, which answers 415 with an application/problem+json ProblemDetails.
// - 500 internal_failure is SafeFailureMiddleware's, for an exception anywhere behind it, so every operation
//   lists it: a ProcessOutput it writes with WriteAsJsonAsync, so application/json whatever was asked for.
// - Kestrel refuses a request before the API sees it while it parses the request line and headers, and
//   answers itself with Content-Length: 0: 400, 408, 414, 431 and 505 (KestrelRefusalTests holds each). Any
//   request can meet them, so every operation lists them, without content where nothing else answers the
//   status and in the description where something else does (400).
// - A request body Kestrel refuses while the API reads it (badly chunked, or slower than the minimum data
//   rate) is a BadHttpRequestException: RequestLimitMiddleware answers its 413, SafeFailureMiddleware its 400
//   request_body_invalid and 408 request_timeout (SafeFailureMiddleware.BodyRefusals), as a ProcessOutput.
//   Only an operation that binds a body reads one, so those list them.
// - No response lists text/plain. ApiExplorer offers every output formatter's media types for every
//   response, and MVC's StringOutputFormatter claims text/plain for any type, but it writes only a string,
//   which no action answers: an envelope asked for as text/plain is written as application/json. The JSON
//   media types an ObjectResult negotiates (application/json, text/json) stay.
internal sealed class ErrorResponseFilter : IOperationFilter
{
    public const string FailureSchema = "FailureOutput";
    private const string PayloadTooLarge = "413";
    private const string UnsupportedMediaType = "415";
    private const string InternalServerError = "500";
    private const string TextPlain = "text/plain";

    private const string KestrelRefused = "Kestrel refused the request before the API saw it, and answered with no body: ";

    // Kestrel's own refusals of a request line or headers, which it answers with an empty body.
    private static readonly (string Status, string Reason, string Cause)[] s_server =
    [
        ("400", "Bad Request", "a malformed request line or header, a missing or invalid Host, or an invalid Content-Length or transfer coding."),
        ("408", "Request Timeout", "its headers arrived slower than the request headers timeout (30 seconds by default)."),
        ("414", "URI Too Long", "its request line is longer than the limit (8 KB by default)."),
        ("431", "Request Header Fields Too Large", "its headers are larger than the limit (32 KB, or 100 headers, by default)."),
        ("505", "HTTP Version Not Supported", "it names an HTTP version Kestrel does not speak.")
    ];

    // A status kept for a reason the document should carry.
    private static readonly Dictionary<(string Action, string Status), string> s_notes = new()
    {
        [(nameof(Controllers.VaultController.Challenge), "409")] =
            "Kept because it cannot be ruled out statically: VaultProtectionStore runs the challenge in a transaction whose "
            + "DbUpdateConcurrencyException catch answers revision_conflict, though the challenge's insert should never raise one."
    };

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var responses = operation.Responses!;
        if (Envelope(context.MethodInfo.ReturnType))
        {
            foreach (var (status, response) in responses)
            {
                if (status is ['4' or '5', ..] && status != PayloadTooLarge && response is OpenApiResponse { Content: { } content })
                {
                    foreach (var media in content.Values.OfType<OpenApiMediaType>())
                        media.Schema = Failure(media.Schema!, context);
                }
            }
        }
        // RFC 9110's name, as Swashbuckle gives it; ReasonPhrases still has the older "Payload Too Large".
        responses[PayloadTooLarge] = new OpenApiResponse
        {
            Description = "Content Too Large. RequestLimitMiddleware refused a Content-Length over the configured limit "
                + "(CERBERUS_MAX_REQUEST_BYTES), or a body read past it or past Kestrel's maximum request body size. No body.",
            Content = new Dictionary<string, IOpenApiMediaType>()
        };
        if (context.ApiDescription.ParameterDescriptions.Any(p => p.Source == BindingSource.Body)
            && context.MethodInfo.GetCustomAttributes(typeof(VaultProofBodyAttribute), inherit: true).Length == 0)
        {
            responses[UnsupportedMediaType] = new OpenApiResponse
            {
                Description = ReasonPhrases.GetReasonPhrase(StatusCodes.Status415UnsupportedMediaType),
                Content = new Dictionary<string, IOpenApiMediaType>
                {
                    ["application/problem+json"] = new OpenApiMediaType
                    { Schema = context.SchemaGenerator.GenerateSchema(typeof(ProblemDetails), context.SchemaRepository) }
                }
            };
        }
        responses[InternalServerError] = new OpenApiResponse
        {
            Description = "Internal Server Error. SafeFailureMiddleware's internal_failure, for an exception anywhere behind it. "
                + "An exception escapes it only while it writes to a client that has already gone, so no client receives "
                + "Kestrel's own bodiless 500.",
            Content = new Dictionary<string, IOpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                { Schema = context.SchemaGenerator.GenerateSchema(typeof(ProcessOutput), context.SchemaRepository) }
            }
        };
        foreach (var (status, reason, cause) in s_server)
        {
            if (responses.TryGetValue(status, out var listed) && listed is OpenApiResponse known)
                known.Description += ". Kestrel can also refuse the request before the API sees it, with no body: " + cause;
            else
                responses[status] = new OpenApiResponse { Description = reason + ". " + KestrelRefused + cause, Content = new Dictionary<string, IOpenApiMediaType>() };
        }
        if (context.ApiDescription.ParameterDescriptions.Any(p => p.Source == BindingSource.Body))
        {
            var known = (OpenApiResponse)responses["400"];
            known.Description += ". Or Kestrel refused the request body as the API read it (badly chunked, or otherwise malformed): "
                + "SafeFailureMiddleware's ProcessOutput, " + SafeFailureMiddleware.BodyRefusals[StatusCodes.Status400BadRequest] + ".";
            var timeout = (OpenApiResponse)responses["408"];
            timeout.Description = "Request Timeout. Kestrel refused the request body as the API read it: it arrived slower than the "
                + "minimum data rate (240 bytes a second, after a 5-second grace, by default). SafeFailureMiddleware's ProcessOutput, "
                + SafeFailureMiddleware.BodyRefusals[StatusCodes.Status408RequestTimeout] + ". Or "
                + KestrelRefused + s_server.Single(server => server.Status == "408").Cause;
            timeout.Content = new Dictionary<string, IOpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType
                { Schema = context.SchemaGenerator.GenerateSchema(typeof(ProcessOutput), context.SchemaRepository) }
            };
        }
        foreach (var ((action, status), note) in s_notes)
        {
            if (context.MethodInfo.Name == action && context.MethodInfo.DeclaringType == typeof(Controllers.VaultController)
                && responses.TryGetValue(status, out var noted) && noted is OpenApiResponse kept)
                kept.Description += ". " + note;
        }
        foreach (var response in responses.Values.OfType<OpenApiResponse>())
            response.Content?.Remove(TextPlain);
        var ordered = responses.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
        responses.Clear();
        foreach (var (status, response) in ordered)
            responses.Add(status, response);
    }

    // ActionResult<DataOutput<T>>, the return type of every action that answers through ToActionResult.
    private static bool Envelope(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            type = type.GetGenericArguments()[0];
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ActionResult<>)
            && type.GetGenericArguments()[0] is { IsGenericType: true } output
            && output.GetGenericTypeDefinition() == typeof(DataOutput<>);
    }

    private static IOpenApiSchema Failure(IOpenApiSchema documented, OperationFilterContext context)
    {
        var failure = new OpenApiSchemaReference(FailureSchema, context.Document);
        if (!context.SchemaRepository.Schemas.ContainsKey(FailureSchema))
        {
            context.SchemaGenerator.GenerateSchema(typeof(ProcessOutput), context.SchemaRepository);
            var process = (OpenApiSchema)context.SchemaRepository.Schemas[nameof(ProcessOutput)];
            var properties = new Dictionary<string, IOpenApiSchema>(process.Properties!)
            {
                ["data"] = new OpenApiSchema
                {
                    Description = "null when the action's handler failed; absent when the request was refused before it ran.",
                    Type = JsonSchemaType.Null
                }
            };
            context.SchemaRepository.AddDefinition(FailureSchema, new OpenApiSchema
            {
                Description = "A failed request's envelope: a handler's DataOutput with null data, or the ProcessOutput "
                    + "of a request refused before its handler ran.",
                Type = JsonSchemaType.Object,
                Properties = properties,
                AdditionalPropertiesAllowed = false
            });
        }
        return documented is OpenApiSchemaReference { Reference.Id: nameof(ProcessOutput) }
            ? failure
            : new OpenApiSchema { AnyOf = [documented, failure] };
    }
}
