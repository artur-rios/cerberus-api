using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Accounts;

public sealed class AccountDeletionOutput:CommandOutput
{
    public Guid AccountId{get;init;}
    public DateTimeOffset DeletedAt{get;init;}
    public required string State{get;init;}
}
public static class AccountDeletionMessages
{
    public static readonly IReadOnlyDictionary<string,int> StatusCodes=new Dictionary<string,int>{
        ["account_deleted"]=200,["account_deletion_pending"]=202,["validation_failed"]=400,["authentication_required"]=401,
        ["fresh_authentication_required"]=401,["vault_access_required"]=403,["vault_access_denied"]=403,["not_found"]=404,
        ["revision_conflict"]=409,["identity_unavailable"]=503,["persistence_unavailable"]=503};
}
