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
internal sealed class ErrorResponseFilter : IOperationFilter
{
    public const string FailureSchema = "FailureOutput";
    private const string PayloadTooLarge = "413";
    private const string UnsupportedMediaType = "415";

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
        responses[PayloadTooLarge] = new OpenApiResponse { Description = ReasonPhrases.GetReasonPhrase(StatusCodes.Status413PayloadTooLarge) };
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
                    Type = JsonSchemaType.Object | JsonSchemaType.Null,
                    Enum = [null!]
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
