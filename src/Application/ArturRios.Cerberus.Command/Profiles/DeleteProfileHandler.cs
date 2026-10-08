using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class DeleteProfileHandler(IValidator<DeleteProfileCommand> validator,IProfileTrashStore store) : ICommandHandlerAsync<DeleteProfileCommand,DeleteProfileOutput>
{
    public async Task<DataOutput<DeleteProfileOutput?>> HandleAsync(DeleteProfileCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();var output=DataOutput<DeleteProfileOutput?>.New;
        if(command.Actor==Guid.Empty)return output.WithError("authentication_required");
        if(command.VaultAccess is null)return output.WithError("vault_access_required");
        if(!OpaqueAccessHandle.TryHash(command.VaultAccess,out var verifier) || !(await validator.ValidateAsync(command,cancellationToken)).IsValid)return output.WithError("validation_failed");
        var result=await store.TrashAsync(new(command.Actor,verifier,command.ProfileId,command.ExpectedRevision),cancellationToken);
        if(result.Error is not null)return output.WithError(result.Error);
        var data=result.Data;
        if(data is null || data.ProfileId!=command.ProfileId || data.TrashOperationId==Guid.Empty
            || data.Revision!=command.ExpectedRevision+1 || data.Revision>ProtocolBinary.MaxInteger
            || data.ServerSequence is <=0 or >ProtocolBinary.MaxInteger
            || data.DeletedAt.Ticks<TimeSpan.TicksPerMicrosecond || data.DeletedAt.Offset!=TimeSpan.Zero
            || data.DeletedAt.Ticks%TimeSpan.TicksPerMicrosecond!=0 || data.PurgeAt.Offset!=TimeSpan.Zero
            || data.PurgeAt-data.DeletedAt!=TimeSpan.FromDays(30))return output.WithError("persistence_unavailable");
        return output.WithData(new DeleteProfileOutput{ProfileId=data.ProfileId,TrashOperationId=data.TrashOperationId,
            Revision=data.Revision,ServerSequence=data.ServerSequence,DeletedAt=data.DeletedAt,PurgeAt=data.PurgeAt}).WithMessage("profile_deleted");
    }
}
