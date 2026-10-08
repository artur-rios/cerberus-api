namespace ArturRios.Cerberus.Command.Protection;

public static class VaultProtectionMessages
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        ["protection_changed"] = 200, ["vault_access_required"] = 401, ["vault_access_denied"] = 403,
        ["protection_initialized"] = 201, ["challenge_issued"] = 201, ["vault_unlocked"] = 200,
        ["validation_failed"] = 400, ["authentication_required"] = 401, ["fresh_authentication_required"] = 401,
        ["vault_proof_rejected"] = 401, ["not_found"] = 404, ["revision_conflict"] = 409,
        ["persistence_unavailable"] = 503, ["identity_unavailable"] = 503
    };
}
