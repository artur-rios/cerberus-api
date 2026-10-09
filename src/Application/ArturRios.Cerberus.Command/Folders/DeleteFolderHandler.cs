using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Folders;

public sealed class DeleteFolderHandler(IValidator<DeleteFolderCommand> validator,IFolderTrashStore store)
    :ICommandHandlerAsync<DeleteFolderCommand,DeleteFolderOutput>
{
    public async Task<DataOutput<DeleteFolderOutput?>> HandleAsync(DeleteFolderCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output=DataOutput<DeleteFolderOutput?>.New;
        if(command.Actor==Guid.Empty)return output.WithError("authentication_required");
        if(command.VaultAccess is null)return output.WithError("vault_access_required");
        if(!OpaqueAccessHandle.TryHash(command.VaultAccess,out var verifier)
            || !(await validator.ValidateAsync(command,cancellationToken)).IsValid)return output.WithError("validation_failed");
        var result=await store.TrashAsync(new(command.Actor,verifier,command.FolderId,command.ExpectedRevision),cancellationToken);
        if(result is null || result.Error is not null && result.Data is not null)return output.WithError("persistence_unavailable");
        if(result.Error is not null)return output.WithError(DeleteFolderMessages.SafeError(result.Error));
        var data=result.Data;
        if(data is null || data.FolderId!=command.FolderId || data.TrashOperationId==Guid.Empty
            || data.Revision!=command.ExpectedRevision+1 || data.Revision is <=0 or >ProtocolBinary.MaxInteger
            || data.ServerSequence is <=0 or >ProtocolBinary.MaxInteger
            || data.DeletedAt.Offset!=TimeSpan.Zero || data.DeletedAt.Ticks<TimeSpan.TicksPerMicrosecond
            || data.DeletedAt.Ticks%TimeSpan.TicksPerMicrosecond!=0 || data.PurgeAt.Offset!=TimeSpan.Zero
            || data.PurgeAt.Ticks%TimeSpan.TicksPerMicrosecond!=0
            || data.PurgeAt-data.DeletedAt!=TimeSpan.FromDays(30))return output.WithError("persistence_unavailable");
        return output.WithData(new DeleteFolderOutput{FolderId=data.FolderId,TrashOperationId=data.TrashOperationId,
            Revision=data.Revision,ServerSequence=data.ServerSequence,DeletedAt=data.DeletedAt,PurgeAt=data.PurgeAt}).WithMessage("folder_deleted");
    }
}
