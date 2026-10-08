namespace ArturRios.Cerberus.Command.Accounts;

public static class UpdateAccountMessages
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        ["account_updated"] = 200, ["validation_failed"] = 400,
        ["authentication_required"] = 401, ["vault_access_required"] = 401,
        ["vault_access_denied"] = 403, ["not_found"] = 404,
        ["revision_conflict"] = 409, ["persistence_unavailable"] = 503
    };
}
