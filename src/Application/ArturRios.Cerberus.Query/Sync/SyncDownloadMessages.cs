namespace ArturRios.Cerberus.Query.Sync;

public static class SyncDownloadMessages
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        ["sync_changes_found"] = 200, ["validation_failed"] = 400,
        ["authentication_required"] = 401, ["vault_access_required"] = 401, ["vault_access_denied"] = 403,
        ["not_found"] = 404, ["resynchronization_required"] = 409, ["revision_conflict"] = 409,
        ["identity_unavailable"] = 503, ["persistence_unavailable"] = 503
    };
}
