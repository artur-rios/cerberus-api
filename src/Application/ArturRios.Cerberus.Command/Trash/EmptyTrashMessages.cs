namespace ArturRios.Cerberus.Command.Trash;
public static class EmptyTrashMessages
{
    public static readonly IReadOnlyDictionary<string,int> StatusCodes=new Dictionary<string,int>{
        ["trash_emptied"]=200,["validation_failed"]=400,["authentication_required"]=401,["vault_access_required"]=401,
        ["vault_access_denied"]=403,["not_found"]=404,["revision_conflict"]=409,["identity_unavailable"]=503,["persistence_unavailable"]=503};
    internal static string SafeError(string error)=>error is "not_found" or "vault_access_denied" or "revision_conflict" or "persistence_unavailable"?error:"persistence_unavailable";
}
