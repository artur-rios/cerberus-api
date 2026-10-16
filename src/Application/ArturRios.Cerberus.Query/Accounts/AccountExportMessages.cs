namespace ArturRios.Cerberus.Query.Accounts;

public static class AccountExportMessages
{
    public static readonly IReadOnlyDictionary<string,int> StatusCodes=new Dictionary<string,int>(StringComparer.Ordinal)
    {
        ["validation_failed"]=400,["authentication_required"]=401,["vault_access_required"]=401,
        ["fresh_authentication_required"]=403,["vault_access_denied"]=403,["not_found"]=404,
        ["revision_conflict"]=409,["persistence_unavailable"]=503
    };
}
