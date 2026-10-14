using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;

namespace ArturRios.Cerberus.Command.Accounts;

public sealed class CancelAccountClosureHandler(IAccountClosureCancellationStore store,TimeProvider clock)
    :ICommandHandlerAsync<CancelAccountClosureCommand,AccountClosureCancellationOutput>
{
    public async Task<DataOutput<AccountClosureCancellationOutput?>> HandleAsync(CancelAccountClosureCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();var output=DataOutput<AccountClosureCancellationOutput?>.New;
        if(command.Actor==Guid.Empty)return output.WithError("authentication_required");
        if(!FreshAuthentication.IsFresh(command.AuthenticationIssuedAt,clock.GetUtcNow()))return output.WithError("fresh_authentication_required");
        if(!new AccountClosureCancellationInput(command.ExpectedRevision).IsValid())return output.WithError("validation_failed");
        var result=await store.CancelAsync(new(command.Actor,command.AuthenticationIssuedAt,command.ExpectedRevision),cancellationToken);
        if(result.Error is{}error)return output.WithError(result.Details is null&&error is
            "validation_failed" or "authentication_required" or "fresh_authentication_required" or "not_found" or "revision_conflict" or "closure_expired" or "persistence_unavailable"?error:"persistence_unavailable");
        var d=result.Details;if(d?.IsValid()!=true||d.Revision!=command.ExpectedRevision+1)return output.WithError("persistence_unavailable");
        return output.WithData(new AccountClosureCancellationOutput{AccountId=d.AccountId,Revision=d.Revision,RevocationGeneration=d.RevocationGeneration,
            CancelledAt=d.CancelledAt,ServerTime=d.ServerTime}).WithMessage("account_closure_cancelled");
    }
}
