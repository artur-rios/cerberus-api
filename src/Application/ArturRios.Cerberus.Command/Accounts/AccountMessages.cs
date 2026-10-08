namespace ArturRios.Cerberus.Command.Accounts;

public static class AccountMessages
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        ["account_registered"] = 201, ["account_registration_replayed"] = 200,
        ["validation_failed"] = 400, ["identity_proof_required"] = 401,
        ["authentication_required"] = 401, ["registration_forbidden"] = 403,
        ["not_found"] = 404, ["registration_conflict"] = 409,
        ["identity_unavailable"] = 503, ["persistence_unavailable"] = 503
    };
}
