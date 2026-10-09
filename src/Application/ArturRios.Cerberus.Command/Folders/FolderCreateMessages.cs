namespace ArturRios.Cerberus.Command.Folders;

public static class FolderCreateMessages
{
    public static readonly IReadOnlyDictionary<string, int> StatusCodes = new Dictionary<string, int>
    {
        ["folder_created"] = 201, ["authentication_required"] = 401, ["vault_access_required"] = 401,
        ["vault_access_denied"] = 403, ["validation_failed"] = 400, ["not_found"] = 404,
        ["revision_conflict"] = 409, ["persistence_unavailable"] = 503, ["identity_unavailable"] = 503
    };
    internal static string SafeError(string error) => StatusCodes.TryGetValue(error, out var status) && status >= 400 ? error : "persistence_unavailable";
}
