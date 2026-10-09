namespace ArturRios.Cerberus.Command.Profiles;
public static class DeleteProfileMessages
{
    public static readonly IReadOnlyDictionary<string,int> StatusCodes=new Dictionary<string,int>
    {
        ["profile_deleted"]=200,["authentication_required"]=401,["vault_access_required"]=401,["vault_access_denied"]=403,
        ["validation_failed"]=400,["not_found"]=404,["revision_conflict"]=409,["persistence_unavailable"]=503,["identity_unavailable"]=503
    };
}
