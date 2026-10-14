namespace ArturRios.Cerberus.Command.Sync;

public static class SyncEditsMessages
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        ["sync_edits_processed"] = 200, ["validation_failed"] = 400,
        ["authentication_required"] = 401, ["vault_access_required"] = 401, ["vault_access_denied"] = 403,
        ["not_found"] = 404, ["operation_conflict"] = 409, ["revision_conflict"] = 409,
        ["identity_unavailable"] = 503, ["persistence_unavailable"] = 503
    };
}
