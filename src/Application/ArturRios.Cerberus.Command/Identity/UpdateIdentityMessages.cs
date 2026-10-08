namespace ArturRios.Cerberus.Command.Identity;

public static class UpdateIdentityMessages
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        ["identity_updated"] = 200, ["validation_failed"] = 400,
        ["authentication_required"] = 401, ["vault_access_required"] = 401,
        ["vault_access_denied"] = 403, ["identity_update_forbidden"] = 403,
        ["not_found"] = 404, ["identity_conflict"] = 409,
        ["identity_unavailable"] = 503, ["persistence_unavailable"] = 503
    };
}
