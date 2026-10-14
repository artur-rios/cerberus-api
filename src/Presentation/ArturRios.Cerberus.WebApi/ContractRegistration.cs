using ArturRios.Output;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.Domain.Profiles;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using ArturRios.Cerberus.WebApi.Controllers;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ArturRios.Cerberus.WebApi;

public static class ContractRegistration
{
    public static IServiceCollection AddCerberusContracts(this IServiceCollection services)
    {
        services.AddControllers().AddApplicationPart(typeof(Program).Assembly)
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.AllowDuplicateProperties = false;
                options.JsonSerializerOptions.PropertyNameCaseInsensitive = false;
                options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict;
                options.JsonSerializerOptions.Converters.Add(new CanonicalGuidConverter());
            })
            .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = _ =>
                new BadRequestObjectResult(ProcessOutput.New.WithError("validation_failed")));
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Cerberus API",
                Version = "v1",
                Description = "Cerberus encrypted vault API. Identity authentication and vault access are separate."
            });
            options.OperationFilter<StrictRequestContentFilter>();
            options.UseOneOfForPolymorphism();
            options.SelectSubTypesUsing(type => type == typeof(SyncResourceData)
                ? [typeof(SyncProfileData), typeof(SyncRecordData), typeof(SyncFolderData), typeof(SyncCollectionData), typeof(SyncGrantData)]
                : []);
            options.SelectDiscriminatorNameUsing(type => type == typeof(SyncResourceData) ? "resourceType" : null);
            options.SelectDiscriminatorValueUsing(type => type.Name switch
            {
                nameof(SyncProfileData) => "profile", nameof(SyncRecordData) => "record", nameof(SyncFolderData) => "folder",
                nameof(SyncCollectionData) => "collection", nameof(SyncGrantData) => "grant", _ => null
            });
            options.SchemaFilter<SyncResourceSchemaFilter>();
        });
        return services;
    }

    // Closed allOf base schemas prohibit derived fields, and record/folder
    // otherwise overlap in oneOf. Flatten these output variants and constrain
    // their discriminator to the exact wire value.
    private sealed class SyncResourceSchemaFilter : ISchemaFilter
    {
        public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
        {
            if (schema is not OpenApiSchema { Properties: not null } target) return;
            if (context.Type == typeof(ProfileKeyWrappers))
            {
                var password = target.Properties["passwordWrapper"];
                if (!HasNullBranch(password)) target.Properties["passwordWrapper"] = new OpenApiSchema
                    { Type = JsonSchemaType.Object | JsonSchemaType.Null, OneOf = [password, NullValue()] };
                return;
            }
            if (context.Type == typeof(SyncChangeItem))
            {
                var data = (OpenApiSchema)target.Properties["data"];
                // OAS 3.0 nullable alone does not extend a type-less oneOf. A
                // distinct null-only branch accepts removal/tombstone data.
                data.Type = null;
                if (!HasNullBranch(data)) data.OneOf!.Add(NullValue());
                return;
            }
            if (!typeof(SyncResourceData).IsAssignableFrom(context.Type) || context.Type == typeof(SyncResourceData)) return;
            target.AllOf?.Clear();
            target.Properties!["resourceId"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" };
            target.Properties["revision"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int64" };
            var kind = context.Type.Name switch
            {
                nameof(SyncProfileData) => "profile", nameof(SyncRecordData) => "record", nameof(SyncFolderData) => "folder",
                nameof(SyncCollectionData) => "collection", nameof(SyncGrantData) => "grant", _ => throw new InvalidOperationException()
            };
            target.Properties["resourceType"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create(kind)] };
            target.Required = target.Properties.Keys.ToHashSet(StringComparer.Ordinal);
        }
        private static bool HasNullBranch(IOpenApiSchema schema) => schema.OneOf?.Any(x => x.Enum?.Count == 1 && x.Enum[0] is null) == true;
        private static OpenApiSchema NullValue() => new() { Type = JsonSchemaType.Object | JsonSchemaType.Null, Enum = [null!] };
    }

    // Documentation only: runtime rejects unsupported media with 400, not MVC's 415.
    private sealed class StrictRequestContentFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var policyUpdate = context.MethodInfo.DeclaringType == typeof(AccountController)
                && context.MethodInfo.Name == nameof(AccountController.SetOfflinePolicy);
            var renewal = context.MethodInfo.DeclaringType == typeof(OfflineAuthorizationsController)
                && context.MethodInfo.Name == nameof(OfflineAuthorizationsController.Renew);
            if (!policyUpdate && !renewal && (context.MethodInfo.DeclaringType != typeof(SoftwareGrantsController)
                || context.MethodInfo.Name is not (nameof(SoftwareGrantsController.GrantAccess)
                    or nameof(SoftwareGrantsController.RevokeAccess)))) return;
            var content = operation.RequestBody!.Content!;
            var json = content["application/json"];
            content.Clear();
            content.Add("application/json", json);
            if (policyUpdate || renewal || context.MethodInfo.Name == nameof(SoftwareGrantsController.RevokeAccess))
            {
                ((OpenApiRequestBody)operation.RequestBody!).Required = true;
                operation.Responses!["413"].Content?.Clear();
            }
            if (context.MethodInfo.Name == nameof(SoftwareGrantsController.RevokeAccess))
            {
                var id = operation.Parameters!.OfType<OpenApiParameter>().Single(p => p.Name == "id" && p.In == ParameterLocation.Path);
                id.Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" };
            }
        }
    }
}
