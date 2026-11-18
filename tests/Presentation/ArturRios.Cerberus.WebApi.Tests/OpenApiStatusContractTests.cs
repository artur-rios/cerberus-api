using System.Globalization;
using ArturRios.Cerberus.Query.Accounts;
using ArturRios.Cerberus.WebApi.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using UtilAllowAnonymous = ArturRios.Util.WebApi.Security.Attributes.AllowAnonymousAttribute;

namespace ArturRios.Cerberus.WebApi.Tests;

// Every operation documents exactly the error statuses it can answer: what its handler can emit through its
// status map, and what the pipeline answers before or around it. Nothing it cannot send, nothing missing.
public sealed class OpenApiStatusContractTests
{
    // The error codes an operation's handler can emit, for an operation whose status map is shared with others
    // that emit more. Every other operation's handler can emit each error code of its map. Read from the
    // handlers and their stores:
    // - InitializeVaultHandler, IssueVaultChallengeHandler and UnlockVaultHandler run in VaultProtectionStore,
    //   which never checks a vault access session: no vault_access_required or vault_access_denied, no
    //   recovery_credential_consumed. ChallengeAsync's revision_conflict is only the shared transaction's
    //   DbUpdateConcurrencyException catch, which an insert cannot raise; that cannot be decided statically, so
    //   it stays listed.
    // - POST /api/vault/recovery is VaultRecoveryStore's "recover" operation, which is permitted without a
    //   session (Permitted is true for it): no vault_access_denied, and no access header to require.
    private static readonly Dictionary<string, string[]> s_emittedErrors = new()
    {
        ["POST /api/vault/protection"] = ["authentication_required", "validation_failed", "not_found", "revision_conflict", "persistence_unavailable"],
        ["POST /api/vault/challenges"] = ["authentication_required", "validation_failed", "not_found", "revision_conflict", "persistence_unavailable"],
        ["POST /api/vault/unlock"] =
            ["authentication_required", "validation_failed", "not_found", "revision_conflict", "vault_proof_rejected", "persistence_unavailable"],
        ["POST /api/vault/recovery"] =
        [
            "authentication_required", "fresh_authentication_required", "validation_failed", "not_found", "revision_conflict",
            "recovery_credential_consumed", "vault_proof_rejected", "persistence_unavailable"
        ]
    };

    [UnitFact]
    public void GivenEveryOperation_WhenGeneratingOpenApi_ThenItDocumentsExactlyTheErrorStatusesItCanAnswer()
    {
        using var host = OpenApiBodies.Host();
        var document = OpenApiBodies.Document(host);
        var actions = OpenApiErrorBodyContractTests.Actions(host).ToDictionary(a => a.Method + " " + a.Path, a => a.Action);
        Assert.Equal(OpenApiAccuracyContractTests.s_operations.Length, actions.Count);
        foreach (var (method, path, map, _) in OpenApiAccuracyContractTests.s_operations)
        {
            var key = method + " " + path;
            var action = actions[key];
            var expected = Pipeline(action);
            if (map is not null)
            {
                // ToActionResult answers 400 for an error its map does not hold.
                expected.Add(400);
                var emitted = s_emittedErrors.TryGetValue(key, out var codes) ? codes : [.. map.Keys];
                Assert.All(emitted, code => Assert.True(map.ContainsKey(code), key + " " + code));
                expected.UnionWith(emitted.Select(code => map[code]).Where(status => status >= 400));
            }
            else if (key == "POST /api/accounts/me/export")
                expected.UnionWith(AccountExportMessages.StatusCodes.Values);
            else if (key == "GET /health/ready")
                expected.Add(503);
            var documented = document["paths"]![path]![method.ToLowerInvariant()]!["responses"]!.AsObject()
                .Select(r => int.Parse(r.Key, CultureInfo.InvariantCulture)).Where(status => status >= 400).Order();
            Assert.Equal(key + ": " + string.Join(", ", expected.Order()), key + ": " + string.Join(", ", documented));
        }
    }

    // Every layer of the API's own that can answer a request, with the statuses the audit found it answers and
    // Pipeline() and the document hold. A middleware or filter added later fails this until it is audited too.
    private static readonly Dictionary<string, int[]> s_layers = new()
    {
        [nameof(NoStoreMiddleware)] = [],
        [nameof(SafeFailureMiddleware)] = [400, 408, 500],
        [nameof(RequestLimitMiddleware)] = [413],
        [nameof(RestoreBarrierMiddleware)] = [503],
        [nameof(ProtectedEndpointMiddleware)] = [401, 403, 503],
        [nameof(VaultProofBodyMiddleware)] = [400],
        [nameof(SyncEditDiscriminatorsAttribute)] = [400]
    };

    [UnitFact]
    public void GivenTheApiAssembly_WhenListingItsMiddlewareAndFilters_ThenEachIsOneTheStatusAuditCovers()
    {
        var layers = typeof(Startup).Assembly.GetTypes()
            .Where(type => typeof(Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata).IsAssignableFrom(type)
                || type.GetMethod("InvokeAsync") is { } invoke && invoke.GetParameters().FirstOrDefault()?.ParameterType == typeof(Microsoft.AspNetCore.Http.HttpContext))
            .Select(type => type.Name).Order();
        Assert.Equal(s_layers.Keys.Order(), layers);
        // MVC's own global filters, as AddCerberusContracts leaves them: the 415 and model-state 400 among them.
        using var host = OpenApiBodies.Host();
        var filters = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Mvc.MvcOptions>>().Value.Filters
            .Select(filter => (filter is Microsoft.AspNetCore.Mvc.TypeFilterAttribute typed ? typed.ImplementationType : filter.GetType()).Name).Order();
        Assert.Equal(["UnsupportedContentTypeFilter"], filters);
    }

    [UnitFact]
    public void GivenTheVaultReads_WhenGeneratingOpenApi_ThenNeitherListsAConflict()
    {
        var document = OpenApiBodies.Document();
        // GetVaultProtectionHandler answers through VaultProtectionQueryMessages, which holds no 409.
        foreach (var path in new[] { "/api/vault/protection", "/api/vault/recovery-material" })
            Assert.Null(document["paths"]![path]!["get"]!["responses"]!["409"]);
        Assert.DoesNotContain(409, VaultProtectionQueryMessagesStatuses());
    }

    private static IEnumerable<int> VaultProtectionQueryMessagesStatuses() =>
        ArturRios.Cerberus.Query.Protection.VaultProtectionQueryMessages.StatusCodes.Values;

    // What the pipeline itself answers for the action, whatever its handler does.
    private static HashSet<int> Pipeline(Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor action)
    {
        var metadata = action.EndpointMetadata;
        // SafeFailureMiddleware's 500 and RequestLimitMiddleware's 413 answer any request, and so do Kestrel's own
        // refusals of a request line or headers (KestrelRefusalTests).
        HashSet<int> statuses = [413, 500, 400, 408, 414, 431, 505];
        var health = metadata.OfType<HealthEndpointAttribute>().Any();
        if (!health)
            statuses.Add(503); // RestoreBarrierMiddleware, which lets only the health routes through.
        var anonymous = metadata.OfType<IAllowAnonymous>().Any() || metadata.OfType<UtilAllowAnonymous>().Any();
        if (!anonymous)
        {
            // ProtectedEndpointMiddleware: 401 for any refused credential; 503 identity_unavailable for a
            // person's or an operator's token; 403 operational_access_required for an operator's.
            statuses.Add(401);
            if (!metadata.OfType<ApplicationAuthenticationAttribute>().Any())
                statuses.Add(503);
            if (metadata.OfType<OperationalAuthenticationAttribute>().Any())
                statuses.Add(403);
        }
        var body = action.Parameters.Any(p => p.BindingInfo?.BindingSource == BindingSource.Body);
        if (metadata.OfType<VaultProofBodyAttribute>().Any())
            statuses.Add(400); // VaultProofBodyMiddleware refuses an unreadable body itself.
        else if (body)
            statuses.Add(415); // MVC answers an unreadable Content-Type.
        if (body)
            statuses.Add(400); // InvalidModelStateResponseFactory.
        return statuses;
    }
}
