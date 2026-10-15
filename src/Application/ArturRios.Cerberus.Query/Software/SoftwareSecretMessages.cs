namespace ArturRios.Cerberus.Query.Software;

public static class SoftwareSecretMessages
{
    public static readonly IReadOnlyDictionary<string,int> StatusCodes=new Dictionary<string,int>
    {
        ["software_secret_found"]=200,["validation_failed"]=400,["authentication_required"]=401,
        ["not_found"]=404,["identity_unavailable"]=503,["persistence_unavailable"]=503
    };
    internal static string SafeStoreError(string error)=>error is "not_found" or "authentication_required" or "persistence_unavailable"?error:"persistence_unavailable";
}
