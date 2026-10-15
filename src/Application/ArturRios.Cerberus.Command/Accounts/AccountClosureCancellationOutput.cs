using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Accounts;

public sealed class AccountClosureCancellationOutput:CommandOutput
{
    public Guid AccountId{get;init;}
    public long Revision{get;init;}
    public long RevocationGeneration{get;init;}
    public DateTimeOffset CancelledAt{get;init;}
    public DateTimeOffset ServerTime{get;init;}
}
public static class AccountClosureCancellationMessages
{
    public static readonly IReadOnlyDictionary<string,int> StatusCodes=new Dictionary<string,int>{
        ["account_closure_cancelled"]=200,["validation_failed"]=400,["authentication_required"]=401,
        ["fresh_authentication_required"]=401,["not_found"]=404,["revision_conflict"]=409,["closure_expired"]=410,
        ["identity_unavailable"]=503,["persistence_unavailable"]=503};
}
