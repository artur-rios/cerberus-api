namespace ArturRios.Cerberus.Command.Records;
public static class DeleteRecordMessages
{
    public static readonly IReadOnlyDictionary<string,int> StatusCodes=new Dictionary<string,int>
    {
        ["record_deleted"]=200,["validation_failed"]=400,["authentication_required"]=401,["vault_access_required"]=401,
        ["vault_access_denied"]=403,["not_found"]=404,["revision_conflict"]=409,["persistence_unavailable"]=503,["identity_unavailable"]=503
    };
    internal static string SafeError(string error)=>error is "validation_failed" or "vault_access_denied" or "not_found" or "revision_conflict" or "persistence_unavailable"?error:"persistence_unavailable";
}
