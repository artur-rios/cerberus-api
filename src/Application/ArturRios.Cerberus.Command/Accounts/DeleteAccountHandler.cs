using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
namespace ArturRios.Cerberus.Command.Accounts;

public sealed class DeleteAccountHandler(IAccountDeletionStore store,TimeProvider clock):ICommandHandlerAsync<DeleteAccountCommand,AccountDeletionOutput>
{
    public async Task<DataOutput<AccountDeletionOutput?>> HandleAsync(DeleteAccountCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();var output=DataOutput<AccountDeletionOutput?>.New;
        if(command.Actor==Guid.Empty)return output.WithError("authentication_required");
        if(!FreshAuthentication.IsFresh(command.AuthenticationIssuedAt,clock.GetUtcNow()))return output.WithError("fresh_authentication_required");
        if(command.VaultAccess is null)return output.WithError("vault_access_required");
        if(!OpaqueAccessHandle.TryHash(command.VaultAccess,out var verifier)||!new AccountDeletionInput(command.ExpectedRevision).IsValid())return output.WithError("validation_failed");
        var result=await store.DeleteAsync(new(command.Actor,command.AuthenticationIssuedAt,verifier,command.ExpectedRevision),cancellationToken);
        if(result.Error is{}error)return output.WithError(result.Details is null&&error is "validation_failed" or "authentication_required" or "fresh_authentication_required"
            or "vault_access_required" or "vault_access_denied" or "not_found" or "revision_conflict" or "persistence_unavailable"?error:"persistence_unavailable");
        var d=result.Details;if(d?.IsValid()!=true)return output.WithError("persistence_unavailable");
        return output.WithData(new AccountDeletionOutput{AccountId=d.AccountId,DeletedAt=d.DeletedAt,State=d.State})
            .WithMessage(d.State=="completed"?"account_deleted":"account_deletion_pending");
    }
}
