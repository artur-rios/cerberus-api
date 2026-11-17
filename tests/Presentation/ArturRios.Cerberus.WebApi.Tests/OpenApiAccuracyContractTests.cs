using System.Reflection;
using System.Text.Json.Nodes;
using ArturRios.Cerberus.Command.Accounts;
using ArturRios.Cerberus.Command.Authentication;
using ArturRios.Cerberus.Command.Collections;
using ArturRios.Cerberus.Command.Folders;
using ArturRios.Cerberus.Command.Identity;
using ArturRios.Cerberus.Command.Offline;
using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.Command.Protection;
using ArturRios.Cerberus.Command.Records;
using ArturRios.Cerberus.Command.Software;
using ArturRios.Cerberus.Command.Sync;
using ArturRios.Cerberus.Command.Trash;
using ArturRios.Cerberus.Query.Accounts;
using ArturRios.Cerberus.Query.Collections;
using ArturRios.Cerberus.Query.Folders;
using ArturRios.Cerberus.Query.Offline;
using ArturRios.Cerberus.Query.Profiles;
using ArturRios.Cerberus.Query.Protection;
using ArturRios.Cerberus.Query.Records;
using ArturRios.Cerberus.Query.Software;
using ArturRios.Cerberus.Query.Sync;
using ArturRios.Cerberus.Query.Trash;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ArturRios.Cerberus.WebApi.Tests;

// The generated clients trust the document strictly: a success code the server can answer must be
// declared (and no other), a body the server requires must be required, and a member the server can
// write as JSON null must be nullable.
public sealed class OpenApiAccuracyContractTests
{
    // Each operation with the status map its action resolves through and the success messages its
    // handler emits (all of the map's success messages when none are named). Operations without a
    // map write their status directly.
    internal static readonly (string Method, string Path, IReadOnlyDictionary<string, int>? Map, string[] Codes)[] s_operations =
    [
        ("DELETE", "/api/accounts/me/permanent", AccountDeletionMessages.StatusCodes, []),
        ("POST", "/api/accounts/me/closure/cancel", AccountClosureCancellationMessages.StatusCodes, []),
        ("POST", "/api/accounts/me/closure", AccountClosureMessages.StatusCodes, []),
        ("PUT", "/api/accounts/me/offline-policy", OfflinePolicyUpdateMessages.StatusCodes, []),
        ("GET", "/api/accounts/me/offline-policy", OfflinePolicyMessages.StatusCodes, []),
        ("PUT", "/api/accounts/me", UpdateAccountMessages.StatusCodes, []),
        ("GET", "/api/accounts/me", AccountQueryMessages.StatusCodes, []),
        ("POST", "/api/accounts", AccountMessages.StatusCodes, []),
        ("POST", "/api/accounts/me/export", null, ["200"]),
        ("POST", "/api/auth/closure/login", ClosureRecoveryAuthenticationMessages.StatusCodes, []),
        ("POST", "/api/auth/closure/2fa/verify", ClosureRecoveryAuthenticationMessages.StatusCodes, []),
        ("POST", "/api/auth/login", AuthenticationMessages.StatusCodes, []),
        ("POST", "/api/auth/2fa/verify", AuthenticationMessages.StatusCodes, []),
        ("DELETE", "/api/collections/{id}/grants/{grantId}", CollectionGrantRevocationMessages.StatusCodes, []),
        ("PUT", "/api/collections/{id}/grants/{grantId}", CollectionGrantChangeMessages.StatusCodes, []),
        ("POST", "/api/collections/{id}/grants", CollectionGrantMessages.StatusCodes, []),
        ("PUT", "/api/collections/{id}/members", SetCollectionMembershipMessages.StatusCodes, []),
        ("DELETE", "/api/collections/{id}", DeleteCollectionMessages.StatusCodes, []),
        ("PUT", "/api/collections/{id}", CollectionUpdateMessages.StatusCodes, []),
        ("GET", "/api/collections/{id}", CollectionReadMessages.StatusCodes, []),
        ("GET", "/api/collections", CollectionListMessages.StatusCodes, []),
        ("POST", "/api/collections", CreateCollectionMessages.StatusCodes, []),
        ("DELETE", "/api/folders/{id}", DeleteFolderMessages.StatusCodes, []),
        ("PUT", "/api/folders/{id}", FolderUpdateMessages.StatusCodes, []),
        ("GET", "/api/folders/{id}", FolderReadMessages.StatusCodes, []),
        ("PUT", "/api/folders/{id}/parent", MoveFolderMessages.StatusCodes, []),
        ("GET", "/api/folders", FolderListMessages.StatusCodes, []),
        ("POST", "/api/folders", FolderCreateMessages.StatusCodes, []),
        ("GET", "/health/live", null, ["200"]),
        ("GET", "/health/ready", null, ["200"]),
        ("GET", "/health/details", null, ["200"]),
        ("PUT", "/api/identity/me", UpdateIdentityMessages.StatusCodes, []),
        ("POST", "/api/offline-authorizations", OfflineAuthorizationMessages.StatusCodes, []),
        ("POST", "/api/profiles/{id}/access/challenges", ProfileAccessMessages.StatusCodes, ["profile_access_challenge_issued"]),
        ("POST", "/api/profiles/{id}/access", ProfileAccessMessages.StatusCodes, ["profile_access_opened"]),
        ("DELETE", "/api/profiles/{id}", DeleteProfileMessages.StatusCodes, []),
        ("PUT", "/api/profiles/{id}", UpdateProfileMessages.StatusCodes, []),
        ("GET", "/api/profiles/{id}", ProfileReadMessages.StatusCodes, []),
        ("PUT", "/api/profiles/{id}/associations", SetProfileAssociationsMessages.StatusCodes, []),
        ("GET", "/api/profiles", ProfileListMessages.StatusCodes, []),
        ("POST", "/api/profiles", ProfileMessages.StatusCodes, []),
        ("PUT", "/api/records/{id}/folder", MoveRecordMessages.StatusCodes, []),
        ("DELETE", "/api/records/{id}/permanent", PermanentlyDeleteRecordMessages.StatusCodes, []),
        ("DELETE", "/api/records/{id}", DeleteRecordMessages.StatusCodes, []),
        ("PUT", "/api/records/{id}", RecordUpdateMessages.StatusCodes, []),
        ("GET", "/api/records/{id}", RecordReadMessages.StatusCodes, []),
        ("GET", "/api/records", RecordListMessages.StatusCodes, []),
        ("POST", "/api/records", RecordCreateMessages.StatusCodes, []),
        ("DELETE", "/api/software-grants/{id}", SoftwareGrantRevocationMessages.StatusCodes, []),
        ("POST", "/api/software-grants", SoftwareGrantMessages.StatusCodes, []),
        ("GET", "/api/software-secrets", SoftwareSecretMessages.StatusCodes, []),
        ("POST", "/api/sync/edits", SyncEditsMessages.StatusCodes, []),
        ("GET", "/api/sync/changes", SyncDownloadMessages.StatusCodes, []),
        ("DELETE", "/api/trash", EmptyTrashMessages.StatusCodes, []),
        ("GET", "/api/trash", TrashListMessages.StatusCodes, []),
        ("POST", "/api/trash/{id}/restore", RestoreTrashMessages.StatusCodes, []),
        ("POST", "/api/vault/protection", VaultProtectionMessages.StatusCodes, ["protection_initialized"]),
        ("PUT", "/api/vault/protection", VaultProtectionMessages.StatusCodes, ["protection_changed"]),
        ("GET", "/api/vault/protection", VaultProtectionQueryMessages.StatusCodes, []),
        ("POST", "/api/vault/recovery", VaultProtectionMessages.StatusCodes, ["recovery_committed"]),
        ("PUT", "/api/vault/recovery", VaultProtectionMessages.StatusCodes, ["recovery_committed"]),
        ("GET", "/api/vault/recovery-material", VaultProtectionQueryMessages.StatusCodes, []),
        ("POST", "/api/vault/challenges", VaultProtectionMessages.StatusCodes, ["challenge_issued"]),
        ("POST", "/api/vault/unlock", VaultProtectionMessages.StatusCodes, ["vault_unlocked"])
    ];

    [UnitFact]
    public void GivenEveryOperation_WhenGeneratingOpenApi_ThenItDeclaresExactlyTheSuccessCodesItsStatusMapAnswers()
    {
        using var host = Host(out _);
        var document = host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var documented = document.Paths.SelectMany(path => path.Value.Operations!.Select(operation =>
            (Method: operation.Key.Method, Path: path.Key, Codes: operation.Value.Responses!.Keys.Where(code => code.StartsWith('2')).Order().ToArray())))
            .ToArray();
        Assert.Equal(s_operations.Select(o => o.Method + " " + o.Path).Order(), documented.Select(o => o.Method + " " + o.Path).Order());
        foreach (var (method, path, map, codes) in s_operations)
        {
            var expected = map is null ? codes
                : map.Where(entry => entry.Value is >= 200 and < 300 && (codes.Length == 0 || codes.Contains(entry.Key)))
                    .Select(entry => entry.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(map is null || codes.All(map.ContainsKey), method + " " + path);
            Assert.Equal(method + " " + path + ": " + string.Join(", ", expected.Distinct().Order()),
                method + " " + path + ": " + string.Join(", ", documented.Single(o => o.Method == method && o.Path == path).Codes));
        }
    }

    [UnitFact]
    public void GivenRegistration_WhenGeneratingOpenApi_ThenCreatedAndReplayedAreBothDeclaredWithARequiredBody()
    {
        using var host = Host(out _);
        var operation = host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1").Paths["/api/accounts"].Operations![HttpMethod.Post];
        Assert.Equal(["200", "201"], operation.Responses!.Keys.Where(code => code.StartsWith('2')).Order());
        Assert.True(operation.RequestBody!.Required);
    }

    [UnitFact]
    public void GivenEveryFromBodyAction_WhenGeneratingOpenApi_ThenItsRequestBodyIsRequired()
    {
        using var host = Host(out _);
        var document = host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        // A missing body never reaches an action: MVC rejects it, so "required" is what the server enforces.
        Assert.False(host.Services.GetRequiredService<IOptions<MvcOptions>>().Value.AllowEmptyInputInBodyModelBinding);
        var bodies = host.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .Where(description => description.ParameterDescriptions.Any(p => p.Source == BindingSource.Body))
            .ToArray();
        Assert.Equal(46, bodies.Length);
        foreach (var description in bodies)
        {
            var operation = document.Paths["/" + description.RelativePath].Operations![HttpMethod.Parse(description.HttpMethod)];
            Assert.True(operation.RequestBody?.Required == true, description.HttpMethod + " /" + description.RelativePath);
        }
        Assert.All(document.Paths.SelectMany(path => path.Value.Operations!.Values).Where(operation => operation.RequestBody is not null),
            operation => Assert.True(operation.RequestBody!.Required));
    }

    [UnitFact]
    public void GivenEveryMemberDeclaredNullable_WhenGeneratingOpenApi_ThenItsSchemaAcceptsNull()
    {
        using var host = Host(out var recorder);
        host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var contracts = host.Services.GetRequiredService<ISerializerDataContractResolver>();
        var nullability = new NullabilityInfoContext();
        var checkedMembers = 0;
        foreach (var (type, schema) in recorder.Schemas)
        {
            if (contracts.GetDataContractForType(type) is not { DataType: DataType.Object, ObjectProperties: { } members })
                continue;
            foreach (var member in members.Where(m => schema.Properties!.ContainsKey(m.Name) && CanBeNull(m.MemberInfo, nullability)))
            {
                checkedMembers++;
                Assert.True(AcceptsNull(schema.Properties![member.Name]), type.Name + "." + member.Name);
            }
        }
        Assert.True(checkedMembers > 0);
    }

    [UnitFact]
    public void GivenSecondFactorLogin_WhenSerializingOpenApi_ThenAuthenticationAccountIsANullableReference()
    {
        var document = Serialized();
        var schemas = document["components"]!["schemas"]!;
        var account = schemas["AuthenticationOutput"]!["properties"]!["account"]!;
        // OpenAPI 3.1: the reference or null, and nothing beside it.
        Assert.Equal(["anyOf"], account.AsObject().Select(keyword => keyword.Key));
        Assert.Collection(account["anyOf"]!.AsArray(),
            reference => Assert.Equal("#/components/schemas/AuthenticationAccount", reference!["$ref"]!.GetValue<string>()),
            nothing => Assert.Equal("null", nothing!["type"]!.GetValue<string>()));
        // Not every reference turns nullable: the login itself is always present.
        Assert.Equal("#/components/schemas/HeimdallLogin", schemas["AuthenticationOutput"]!["properties"]!["identity"]!["$ref"]!.GetValue<string>());
    }

    private static WebApplication Host(out SchemaRecorder recorder)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(Startup).Assembly.GetName().Name, Args = [] });
        builder.Services.AddCerberusContracts();
        var instance = recorder = new SchemaRecorder();
        // Registered after the contract's own filters, so it sees each schema as published.
        builder.Services.Configure<SwaggerGenOptions>(options => options.AddSchemaFilterInstance(instance));
        return builder.Build();
    }

    private static JsonNode Serialized()
    {
        using var host = Host(out _);
        var document = host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        using var text = new StringWriter();
        ContractRegistration.WriteContract(document, text);
        return JsonNode.Parse(text.ToString())!;
    }

    // The member can be written as JSON null: a Nullable<T>, or a reference the annotations call
    // nullable. DataOutput's generic `data` is set on every success and is not counted.
    private static bool CanBeNull(MemberInfo? member, NullabilityInfoContext nullability)
    {
        if (member is not PropertyInfo property || property.DeclaringType is { IsGenericType: true } declaring
            && declaring.GetGenericTypeDefinition().GetProperty(property.Name)!.PropertyType.IsGenericParameter)
            return false;
        return Nullable.GetUnderlyingType(property.PropertyType) is not null
            || !property.PropertyType.IsValueType && nullability.Create(property).ReadState == NullabilityState.Nullable;
    }

    private static bool AcceptsNull(IOpenApiSchema schema) => schema is OpenApiSchema
        && (schema.Type?.HasFlag(JsonSchemaType.Null) == true || schema.OneOf?.Any(AcceptsNull) == true
            || schema.AnyOf?.Any(AcceptsNull) == true);

    private sealed class SchemaRecorder : ISchemaFilter
    {
        public Dictionary<Type, OpenApiSchema> Schemas { get; } = [];

        public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
        {
            if (schema is OpenApiSchema { Properties.Count: > 0 } target && context.MemberInfo is null && context.ParameterInfo is null)
                Schemas[context.Type] = target;
        }
    }
}
