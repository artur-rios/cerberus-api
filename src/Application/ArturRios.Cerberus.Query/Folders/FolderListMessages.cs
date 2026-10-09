namespace ArturRios.Cerberus.Query.Folders;

public static class FolderListMessages
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        ["folders_found"] = 200, ["validation_failed"] = 400,
        ["authentication_required"] = 401, ["vault_access_required"] = 401,
        ["vault_access_denied"] = 403, ["not_found"] = 404,
        ["identity_unavailable"] = 503, ["persistence_unavailable"] = 503
    };
    internal static string SafeError(string error) => error is "not_found" or "vault_access_denied" or "persistence_unavailable"
        ? error : "persistence_unavailable";
}
