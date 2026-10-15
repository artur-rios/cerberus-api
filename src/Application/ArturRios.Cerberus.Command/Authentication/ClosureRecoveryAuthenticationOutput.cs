using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Authentication;

public sealed class ClosureRecoveryAuthenticationOutput:CommandOutput
{
    public required HeimdallLogin Identity{get;init;}
    public AccountClosureRecoveryDetails? Account{get;init;}
}
public static class ClosureRecoveryAuthenticationMessages
{
    public static readonly IReadOnlyDictionary<string,int> StatusCodes=new Dictionary<string,int>{
        ["closure_recovery_authenticated"]=200,["authentication_challenge_required"]=200,["validation_failed"]=400,
        ["authentication_required"]=401,["fresh_authentication_required"]=401,["authentication_forbidden"]=403,
        ["closure_expired"]=410,["identity_unavailable"]=503,["persistence_unavailable"]=503};
}
