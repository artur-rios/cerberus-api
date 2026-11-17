using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.WebApi.Controllers;
using ArturRios.Output;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ArturRios.Cerberus.WebApi;

public static class ContractRegistration
{
    // Writes the contract (tools/OpenApiGen, docs/contracts/openapi.json) as OpenAPI 3.1. 3.0's nullable counts only
    // beside a type, so a nullable reference has no 3.0 spelling that a client generator (swagger_parser 2) reads as
    // nullable; 3.1 has JSON Schema's "null" type, which a reference joins in anyOf. The API serves no /swagger
    // endpoint, so this is the only place the document is written.
    public static void WriteContract(OpenApiDocument document, TextWriter output) =>
        document.SerializeAsV31(new OpenApiJsonWriter(output));

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
                : type == typeof(AccountExportEntry) ? AccountExportSchemaFilter.s_kinds.Keys
                : type == typeof(TrashListItem)
                ? [typeof(TrashRecordItem), typeof(TrashFolderItem), typeof(TrashCollectionItem), typeof(TrashProfileItem)]
                : type == typeof(SyncEditInput)
                ? [typeof(SyncRecordCreate), typeof(SyncRecordUpdate), typeof(SyncFolderCreate), typeof(SyncFolderUpdate),
                    typeof(SyncCollectionCreate), typeof(SyncCollectionUpdate), typeof(SyncProfileCreate), typeof(SyncProfileUpdate)]
                : []);
            options.SelectDiscriminatorNameUsing(type => type == typeof(AccountExportEntry) ? "entryType" : type == typeof(SyncResourceData) ? "resourceType" : type == typeof(SyncEditInput) ? "editType" : type == typeof(TrashListItem) ? "kind" : null);
            options.SelectDiscriminatorValueUsing(type => AccountExportSchemaFilter.s_kinds.TryGetValue(type, out var exportKind) ? exportKind : type.Name switch
            {
                nameof(TrashRecordItem) => "record",
                nameof(TrashFolderItem) => "folder",
                nameof(TrashCollectionItem) => "collection",
                nameof(TrashProfileItem) => "profile",
                nameof(SyncProfileData) => "profile",
                nameof(SyncRecordData) => "record",
                nameof(SyncFolderData) => "folder",
                nameof(SyncCollectionData) => "collection",
                nameof(SyncGrantData) => "grant",
                nameof(SyncRecordCreate) => "record-create",
                nameof(SyncRecordUpdate) => "record-update",
                nameof(SyncFolderCreate) => "folder-create",
                nameof(SyncFolderUpdate) => "folder-update",
                nameof(SyncCollectionCreate) => "collection-create",
                nameof(SyncCollectionUpdate) => "collection-update",
                nameof(SyncProfileCreate) => "profile-create",
                nameof(SyncProfileUpdate) => "profile-update",
                _ => null
            });
            options.SchemaFilter<SyncResourceSchemaFilter>();
            options.SchemaFilter<TrashListSchemaFilter>();
            options.SchemaFilter<TrashRestoreSchemaFilter>();
            options.SchemaFilter<TrashEmptySchemaFilter>();
            options.SchemaFilter<AccountExportSchemaFilter>();
            options.SchemaFilter<HealthSchemaFilter>();
            options.OperationFilter<HealthSchemaFilter>();
            // Last, so the hand-shaped members above keep their own null branches.
            options.SchemaFilter<NullableReferenceSchemaFilter>();
            options.OperationFilter<RequiredBodyFilter>();
            // After the filters above, which shape some of the responses it describes.
            options.OperationFilter<ErrorResponseFilter>();
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
            if (schema is not OpenApiSchema { Properties: not null } target)
                return;
            if (context.Type == typeof(ArturRios.Cerberus.Command.Sync.UploadSyncEditsCommand))
            {
                var edits = (OpenApiSchema)target.Properties["edits"];
                edits.Type = JsonSchemaType.Array;
                edits.MinItems = 1;
                return;
            }
            if (context.Type == typeof(SyncEditOutcome))
            {
                target.Properties["resourceKind"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Enum = [JsonValue.Create("profile"), JsonValue.Create("record"), JsonValue.Create("folder"), JsonValue.Create("collection")]
                };
                target.Properties["status"] = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Enum = [JsonValue.Create("applied"), JsonValue.Create("superseded"), JsonValue.Create("denied"), JsonValue.Create("permanently-deleted")]
                };
                target.Required = target.Properties.Keys.ToHashSet(StringComparer.Ordinal);
                return;
            }
            if (context.Type == typeof(ArturRios.Cerberus.Command.Accounts.AccountClosureOutput)
                || context.Type == typeof(ArturRios.Cerberus.Command.Accounts.AccountClosureCancellationOutput)
                || context.Type == typeof(ArturRios.Cerberus.Command.Accounts.AccountDeletionOutput)
                || context.Type == typeof(ArturRios.Cerberus.Domain.Accounts.AccountClosureRecoveryDetails))
            {
                target.Required = target.Properties.Keys.ToHashSet(StringComparer.Ordinal);
                if (context.Type == typeof(ArturRios.Cerberus.Command.Accounts.AccountDeletionOutput))
                    target.Properties["state"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create("completed"), JsonValue.Create("pending")] };
                return;
            }
            if (typeof(SyncEditInput).IsAssignableFrom(context.Type) && !context.Type.IsAbstract)
            {
                target.AllOf?.Clear();
                target.Properties["operationId"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" };
                var editKind = context.Type.Name switch
                {
                    nameof(SyncRecordCreate) => "record-create",
                    nameof(SyncRecordUpdate) => "record-update",
                    nameof(SyncFolderCreate) => "folder-create",
                    nameof(SyncFolderUpdate) => "folder-update",
                    nameof(SyncCollectionCreate) => "collection-create",
                    nameof(SyncCollectionUpdate) => "collection-update",
                    nameof(SyncProfileCreate) => "profile-create",
                    nameof(SyncProfileUpdate) => "profile-update",
                    _ => throw new InvalidOperationException()
                };
                target.Properties["editType"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create(editKind)] };
                target.Required = target.Properties.Keys.ToHashSet(StringComparer.Ordinal);
                return;
            }
            if (context.Type == typeof(ArturRios.Cerberus.Command.Authentication.ClosureRecoveryAuthenticationOutput))
            {
                // OAS 3.1's nullable reference: the reference or null.
                target.Properties["account"] = new OpenApiSchema { AnyOf = [target.Properties["account"], NullValue()] };
                return;
            }
            if (context.Type == typeof(ProfileKeyWrappers))
            {
                var password = target.Properties["passwordWrapper"];
                if (!HasNullBranch(password))
                    target.Properties["passwordWrapper"] = new OpenApiSchema { AnyOf = [password, NullValue()] };
                return;
            }
            if (context.Type == typeof(SyncChangeItem))
            {
                var data = (OpenApiSchema)target.Properties["data"];
                // A distinct null-only branch accepts removal/tombstone data.
                // Swashbuckle 11 appends a null-only branch of its own; publish
                // the resource variants and the single OAS 3.1 null branch,
                // with no outer type, which would only repeat the branches'.
                data.OneOf = [.. data.OneOf!.Where(branch => !IsNullOnly(branch)), NullValue()];
                data.Type = null;
                return;
            }
            if (!typeof(SyncResourceData).IsAssignableFrom(context.Type) || context.Type == typeof(SyncResourceData))
                return;
            target.AllOf?.Clear();
            target.Properties!["resourceId"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" };
            target.Properties["revision"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int64" };
            var kind = context.Type.Name switch
            {
                nameof(SyncProfileData) => "profile",
                nameof(SyncRecordData) => "record",
                nameof(SyncFolderData) => "folder",
                nameof(SyncCollectionData) => "collection",
                nameof(SyncGrantData) => "grant",
                _ => throw new InvalidOperationException()
            };
            target.Properties["resourceType"] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [JsonValue.Create(kind)] };
            target.Required = target.Properties.Keys.ToHashSet(StringComparer.Ordinal);
        }
        private static bool HasNullBranch(IOpenApiSchema schema) => schema.OneOf?.Any(IsNullOnly) == true || schema.AnyOf?.Any(IsNullOnly) == true;
        // Swashbuckle 11's own null branch is a schema of type null with a null enum value;
        // Microsoft.OpenApi 3 reads a null enum value as JsonNullSentinel rather than a C# null.
        private static bool IsNullOnly(IOpenApiSchema schema) => schema is OpenApiSchema
            && (schema.Type == JsonSchemaType.Null || schema.Enum is [var only] && (only is null || only.IsJsonNullSentinel()));
        // OAS 3.1's null branch: {"type": "null"}.
        private static OpenApiSchema NullValue() => new() { Type = JsonSchemaType.Null };
    }

    // Documentation only: runtime rejects unsupported media with 400, not MVC's 415.
    private sealed class StrictRequestContentFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var policyUpdate = context.MethodInfo.DeclaringType == typeof(AccountController)
                && context.MethodInfo.Name == nameof(AccountController.SetOfflinePolicy);
            var export = context.MethodInfo.DeclaringType == typeof(AccountExportController);
            var closure = context.MethodInfo.DeclaringType == typeof(AccountController)
                && context.MethodInfo.Name is nameof(AccountController.RequestClosure) or nameof(AccountController.CancelClosure) or nameof(AccountController.DeleteAccount);
            var recovery = context.MethodInfo.DeclaringType == typeof(AuthController)
                && context.MethodInfo.Name is nameof(AuthController.ClosureLogin) or nameof(AuthController.ClosureVerifyChallenge);
            var renewal = context.MethodInfo.DeclaringType == typeof(OfflineAuthorizationsController)
                && context.MethodInfo.Name == nameof(OfflineAuthorizationsController.Renew);
            var trashEmpty = context.MethodInfo.DeclaringType == typeof(TrashController) && context.MethodInfo.Name == nameof(TrashController.EmptyTrash);
            var trashRestore = context.MethodInfo.DeclaringType == typeof(TrashController) && context.MethodInfo.Name == nameof(TrashController.Restore);
            var syncEdits = context.MethodInfo.DeclaringType == typeof(SyncController) && context.MethodInfo.Name == nameof(SyncController.Edits);
            if (!export && !trashEmpty && !trashRestore && !recovery && !closure && !policyUpdate && !renewal && !syncEdits && (context.MethodInfo.DeclaringType != typeof(SoftwareGrantsController)
                || context.MethodInfo.Name is not (nameof(SoftwareGrantsController.GrantAccess)
                    or nameof(SoftwareGrantsController.RevokeAccess))))
                return;
            var content = operation.RequestBody!.Content!;
            var json = content["application/json"];
            content.Clear();
            content.Add("application/json", json);
            if (export || trashEmpty || trashRestore || recovery || closure || policyUpdate || renewal || syncEdits || context.MethodInfo.Name == nameof(SoftwareGrantsController.RevokeAccess))
                operation.Responses!["413"].Content?.Clear();
            if (export)
                ((OpenApiParameter)operation.Parameters!.Single(p => p.Name == "X-Cerberus-Vault-Access")).Required = true;
            if (trashRestore || context.MethodInfo.Name == nameof(SoftwareGrantsController.RevokeAccess))
            {
                var id = operation.Parameters!.OfType<OpenApiParameter>().Single(p => p.Name == "id" && p.In == ParameterLocation.Path);
                id.Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" };
            }
        }
    }
}
