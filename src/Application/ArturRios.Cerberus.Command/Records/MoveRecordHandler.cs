using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Records;

public sealed class MoveRecordHandler(IValidator<MoveRecordCommand> validator,IRecordMoveStore store)
    :ICommandHandlerAsync<MoveRecordCommand,MoveRecordOutput>
{
    public async Task<DataOutput<MoveRecordOutput?>> HandleAsync(MoveRecordCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output=DataOutput<MoveRecordOutput?>.New;
        if(command.Actor==Guid.Empty)return output.WithError("authentication_required");
        if(command.VaultAccess is null)return output.WithError("vault_access_required");
        if(!OpaqueAccessHandle.TryHash(command.VaultAccess,out var verifier)
            || !(await validator.ValidateAsync(command,cancellationToken)).IsValid)return output.WithError("validation_failed");
        var result=await store.MoveAsync(new(command.Actor,verifier,command.RecordId,command.ExpectedRevision,command.FolderId),cancellationToken);
        if(result is null || result.Error is not null && result.Data is not null)return output.WithError("persistence_unavailable");
        if(result.Error is not null)return output.WithError(MoveRecordMessages.SafeError(result.Error));
        var data=result.Data;
        if(data is null || data.RecordId!=command.RecordId || data.FolderId!=command.FolderId
            || data.Revision!=command.ExpectedRevision+1 || data.Revision is <=0 or >ProtocolBinary.MaxInteger
            || data.ServerSequence is <=0 or >ProtocolBinary.MaxInteger)return output.WithError("persistence_unavailable");
        return output.WithData(new MoveRecordOutput{RecordId=data.RecordId,FolderId=data.FolderId,
            Revision=data.Revision,ServerSequence=data.ServerSequence}).WithMessage("record_moved");
    }
}
