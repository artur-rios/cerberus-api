using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Folders;

public sealed class MoveFolderHandler(IValidator<MoveFolderCommand> validator,IFolderMoveStore store)
    :ICommandHandlerAsync<MoveFolderCommand,MoveFolderOutput>
{
    public async Task<DataOutput<MoveFolderOutput?>> HandleAsync(MoveFolderCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output=DataOutput<MoveFolderOutput?>.New;
        if(command.Actor==Guid.Empty)return output.WithError("authentication_required");
        if(command.VaultAccess is null)return output.WithError("vault_access_required");
        if(!OpaqueAccessHandle.TryHash(command.VaultAccess,out var verifier)
            || !(await validator.ValidateAsync(command,cancellationToken)).IsValid)return output.WithError("validation_failed");
        var result=await store.MoveAsync(new(command.Actor,verifier,command.FolderId,command.ExpectedRevision,command.ParentFolderId),cancellationToken);
        if(result is null || result.Error is not null && result.Data is not null)return output.WithError("persistence_unavailable");
        if(result.Error is not null)return output.WithError(MoveFolderMessages.SafeError(result.Error));
        var data=result.Data;
        if(data is null || data.FolderId!=command.FolderId || data.ParentFolderId!=command.ParentFolderId
            || data.Revision!=command.ExpectedRevision+1 || data.Revision is <=0 or >ProtocolBinary.MaxInteger
            || data.ServerSequence is <=0 or >ProtocolBinary.MaxInteger)return output.WithError("persistence_unavailable");
        return output.WithData(new MoveFolderOutput{FolderId=data.FolderId,ParentFolderId=data.ParentFolderId,
            Revision=data.Revision,ServerSequence=data.ServerSequence}).WithMessage("folder_moved");
    }
}
