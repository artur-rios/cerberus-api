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
                options.JsonSerializerOptions.AllowOutOfOrderMetadataProperties = true;
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
                : type == typeof(SyncEditInput)
                ? [typeof(SyncRecordCreate), typeof(SyncRecordUpdate), typeof(SyncFolderCreate), typeof(SyncFolderUpdate),
                    typeof(SyncCollectionCreate), typeof(SyncCollectionUpdate), typeof(SyncProfileCreate), typeof(SyncProfileUpdate)]
                : []);
            options.SelectDiscriminatorNameUsing(type => type == typeof(SyncResourceData) ? "resourceType" : type == typeof(SyncEditInput) ? "editType" : null);
            options.SelectDiscriminatorValueUsing(type => type.Name switch
            {
                nameof(SyncProfileData) => "profile", nameof(SyncRecordData) => "record", nameof(SyncFolderData) => "folder",
                nameof(SyncCollectionData) => "collection", nameof(SyncGrantData) => "grant",
                nameof(SyncRecordCreate) => "record-create", nameof(SyncRecordUpdate) => "record-update",
                nameof(SyncFolderCreate) => "folder-create", nameof(SyncFolderUpdate) => "folder-update",
                nameof(SyncCollectionCreate) => "collection-create", nameof(SyncCollectionUpdate) => "collection-update",
                nameof(SyncProfileCreate) => "profile-create", nameof(SyncProfileUpdate) => "profile-update", _ => null
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
            if (context.Type == typeof(ArturRios.Cerberus.Command.Sync.UploadSyncEditsCommand))
            {
                var edits = (OpenApiSchema)target.Properties["edits"];
                edits.Type = JsonSchemaType.Array;
                edits.MinItems = 1;
                return;
            }
            if (context.Type == typeof(SyncEditOutcome))
            {
                target.Properties["resourceKind"] = new OpenApiSchema { Type = JsonSchemaType.String,
                    Enum = [JsonValue.Create("profile"), JsonValue.Create("record"), JsonValue.Create("folder"), JsonValue.Create("collection")] };
                target.Properties["status"] = new OpenApiSchema { Type = JsonSchemaType.String,
                    Enum = [JsonValue.Create("applied"), JsonValue.Create("superseded"), JsonValue.Create("denied"), JsonValue.Create("permanently-deleted")] };
                target.Required = target.Properties.Keys.ToHashSet(StringComparer.Ordinal);
                return;
            }
            if (context.Type == typeof(ArturRios.Cerberus.Command.Accounts.AccountClosureOutput)
                ||context.Type==typeof(ArturRios.Cerberus.Command.Accounts.AccountClosureCancellationOutput)
                ||context.Type==typeof(ArturRios.Cerberus.Domain.Accounts.AccountClosureRecoveryDetails))
            {
                target.Required = target.Properties.Keys.ToHashSet(StringComparer.Ordinal);
                return;
            }
            if (typeof(SyncEditInput).IsAssignableFrom(context.Type) && !context.Type.IsAbstract)
            {
                target.AllOf?.Clear();
                target.Properties["operationId"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" };
                var editKind = context.Type.Name switch
                {
                    nameof(SyncRecordCreate) => "record-create", nameof(SyncRecordUpdate) => "record-update",
                    nameof(SyncFolderCreate) => "folder-create", nameof(SyncFolderUpdate) => "folder-update",
                    nameof(SyncCollectionCreate) => "collection-create", nameof(SyncCollectionUpdate) => "collection-update",
                    nameof(SyncProfileCreate) => "profile-create", nameof(SyncProfileUpdate) => "profile-update", _ => throw new InvalidOperationException()
                };
                target.Properties["editType"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create(editKind)] };
                target.Required = target.Properties.Keys.ToHashSet(StringComparer.Ordinal);
                return;
            }
            if (context.Type == typeof(ArturRios.Cerberus.Command.Authentication.ClosureRecoveryAuthenticationOutput))
            {
                var account = target.Properties["account"];
                target.Properties["account"] = new OpenApiSchema
                    { Type = JsonSchemaType.Object | JsonSchemaType.Null, OneOf = [account, NullValue()] };
                return;
            }
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
            var closure = context.MethodInfo.DeclaringType == typeof(AccountController)
                && context.MethodInfo.Name is nameof(AccountController.RequestClosure) or nameof(AccountController.CancelClosure);
            var recovery=context.MethodInfo.DeclaringType==typeof(AuthController)
                &&context.MethodInfo.Name is nameof(AuthController.ClosureLogin) or nameof(AuthController.ClosureVerifyChallenge);
            var renewal = context.MethodInfo.DeclaringType == typeof(OfflineAuthorizationsController)
                && context.MethodInfo.Name == nameof(OfflineAuthorizationsController.Renew);
            var syncEdits = context.MethodInfo.DeclaringType == typeof(SyncController) && context.MethodInfo.Name == nameof(SyncController.Edits);
            if (!recovery && !closure && !policyUpdate && !renewal && !syncEdits && (context.MethodInfo.DeclaringType != typeof(SoftwareGrantsController)
                || context.MethodInfo.Name is not (nameof(SoftwareGrantsController.GrantAccess)
                    or nameof(SoftwareGrantsController.RevokeAccess)))) return;
            var content = operation.RequestBody!.Content!;
            var json = content["application/json"];
            content.Clear();
            content.Add("application/json", json);
            if (recovery || closure || policyUpdate || renewal || syncEdits || context.MethodInfo.Name == nameof(SoftwareGrantsController.RevokeAccess))
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
