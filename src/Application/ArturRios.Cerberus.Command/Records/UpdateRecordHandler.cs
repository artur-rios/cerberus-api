using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Records;

public sealed class UpdateRecordHandler(IValidator<UpdateRecordCommand> validator,IRecordUpdateStore store)
    :ICommandHandlerAsync<UpdateRecordCommand,UpdateRecordOutput>
{
    public async Task<DataOutput<UpdateRecordOutput?>> HandleAsync(UpdateRecordCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output=DataOutput<UpdateRecordOutput?>.New;
        if(command.Actor==Guid.Empty)return output.WithError("authentication_required");
        if(command.VaultAccess is null)return output.WithError("vault_access_required");
        if(!OpaqueAccessHandle.TryHash(command.VaultAccess,out var verifier)
            || !(await validator.ValidateAsync(command,cancellationToken)).IsValid)return output.WithError("validation_failed");
        var result=await store.UpdateAsync(new(command.Actor,verifier,command.RecordId,command.ToInput()),cancellationToken);
        if(result is null || result.Error is not null && result.Data is not null)return output.WithError("persistence_unavailable");
        if(result.Error is not null)return output.WithError(RecordUpdateMessages.SafeError(result.Error));
        var data=result.Data;
        var edited=command.EditedAt.AddTicks(-(command.EditedAt.Ticks%TimeSpan.TicksPerMicrosecond));
        if(data is null || data.RecordId!=command.RecordId || data.Revision!=command.ExpectedRevision+1
            || data.Revision is <=0 or >ProtocolBinary.MaxInteger || data.ServerSequence is <=0 or >ProtocolBinary.MaxInteger
            || data.EditedAt.Offset!=TimeSpan.Zero || data.EditedAt.Ticks<TimeSpan.TicksPerMicrosecond
            || data.EditedAt.Ticks%TimeSpan.TicksPerMicrosecond!=0 || data.EditedAt!=edited)return output.WithError("persistence_unavailable");
        return output.WithData(new UpdateRecordOutput{RecordId=data.RecordId,Revision=data.Revision,ServerSequence=data.ServerSequence,EditedAt=data.EditedAt}).WithMessage("record_updated");
    }
}
