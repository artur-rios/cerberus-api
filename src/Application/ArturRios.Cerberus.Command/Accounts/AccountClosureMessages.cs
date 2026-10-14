namespace ArturRios.Cerberus.Command.Accounts;

public static class AccountClosureMessages
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        ["account_closure_pending"] = 200, ["validation_failed"] = 400,
        ["authentication_required"] = 401, ["fresh_authentication_required"] = 401,
        ["not_found"] = 404, ["revision_conflict"] = 409,
        ["identity_unavailable"] = 503, ["persistence_unavailable"] = 503
    };
}
