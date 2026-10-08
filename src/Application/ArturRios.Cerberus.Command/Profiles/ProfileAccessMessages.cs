namespace ArturRios.Cerberus.Command.Profiles;
public static class ProfileAccessMessages
{
    public static readonly IReadOnlyDictionary<string,int> StatusCodes=new Dictionary<string,int>
    {
        ["profile_access_opened"]=200,["profile_access_challenge_issued"]=201,
        ["authentication_required"]=401,["vault_proof_rejected"]=401,["validation_failed"]=400,
        ["not_found"]=404,["revision_conflict"]=409,["persistence_unavailable"]=503,["identity_unavailable"]=503
    };
    internal static string SafeError(string error)=>StatusCodes.TryGetValue(error,out var status) && status>=400?error:"persistence_unavailable";
}
