namespace ArturRios.Cerberus.Query.Profiles;
public static class ProfileReadMessages
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        ["profile_found"] = 200, ["validation_failed"] = 400,
        ["authentication_required"] = 401, ["vault_access_required"] = 401,
        ["vault_access_denied"] = 403, ["not_found"] = 404,
        ["identity_unavailable"] = 503, ["persistence_unavailable"] = 503
    };
}
