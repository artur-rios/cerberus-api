using System.Text.Json.Nodes;
using ArturRios.Cerberus.Shared.Operations;
using ArturRios.Cerberus.WebApi.Controllers;
using ArturRios.Output;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ArturRios.Cerberus.WebApi;

internal sealed class HealthSchemaFilter : ISchemaFilter, IOperationFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema { Properties: not null } target
            || context.Type != typeof(HealthStatus) && context.Type != typeof(HealthDetails) && context.Type != typeof(HealthComponent)) return;
        target.AdditionalPropertiesAllowed = false;
        target.Required = target.Properties.Keys.ToHashSet(StringComparer.Ordinal);
        target.Properties["status"] = Text("healthy", "unhealthy");
        if (context.Type == typeof(HealthComponent))
        {
            target.Properties["name"] = Text("database", "identity", "restore");
            ((OpenApiSchema)target.Properties["elapsedMilliseconds"]).Minimum = "0";
        }
        if (context.Type == typeof(HealthDetails))
        {
            var components = (OpenApiSchema)target.Properties["components"];
            components.MinItems = components.MaxItems = 3;
        }
    }

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.MethodInfo.DeclaringType != typeof(HealthController)) return;
        if (context.MethodInfo.Name == nameof(HealthController.Details))
        {
            operation.Responses!["503"].Content!["application/json"].Schema = new OpenApiSchema
            {
                OneOf = [context.SchemaGenerator.GenerateSchema(typeof(HealthDetails), context.SchemaRepository),
                    context.SchemaGenerator.GenerateSchema(typeof(ProcessOutput), context.SchemaRepository)]
            };
            operation.Responses["503"].Description = "Unhealthy authorized components, or identity_unavailable without component details.";
        }
    }

    private static OpenApiSchema Text(params string[] values) => new()
    { Type = JsonSchemaType.String, Enum = values.Select(v => (JsonNode)JsonValue.Create(v)!).ToList() };
}
