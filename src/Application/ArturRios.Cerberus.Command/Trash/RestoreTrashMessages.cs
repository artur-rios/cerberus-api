namespace ArturRios.Cerberus.Command.Trash;
public static class RestoreTrashMessages
{
    public static readonly IReadOnlyDictionary<string,int> StatusCodes=new Dictionary<string,int>
    {
        ["trash_restored"]=200,["validation_failed"]=400,["authentication_required"]=401,["vault_access_required"]=401,
        ["vault_access_denied"]=403,["not_found"]=404,["revision_conflict"]=409,["trash_expired"]=410,
        ["identity_unavailable"]=503,["persistence_unavailable"]=503
    };
    internal static string SafeError(string error)=>error is "not_found" or "vault_access_denied" or "revision_conflict" or "trash_expired" or "persistence_unavailable"
        ?error:"persistence_unavailable";
}
