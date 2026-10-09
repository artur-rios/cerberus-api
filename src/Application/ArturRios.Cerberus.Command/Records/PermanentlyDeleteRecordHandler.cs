using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Records;

public sealed class PermanentlyDeleteRecordHandler(IValidator<PermanentlyDeleteRecordCommand> validator,IRecordPermanentDeleteStore store)
    :ICommandHandlerAsync<PermanentlyDeleteRecordCommand,PermanentlyDeleteRecordOutput>
{
    public async Task<DataOutput<PermanentlyDeleteRecordOutput?>> HandleAsync(PermanentlyDeleteRecordCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output=DataOutput<PermanentlyDeleteRecordOutput?>.New;
        if(command.Actor==Guid.Empty)return output.WithError("authentication_required");
        if(command.VaultAccess is null)return output.WithError("vault_access_required");
        if(!OpaqueAccessHandle.TryHash(command.VaultAccess,out var verifier)
            || !(await validator.ValidateAsync(command,cancellationToken)).IsValid)return output.WithError("validation_failed");
        var result=await store.DeleteAsync(new(command.Actor,verifier,command.RecordId,command.ExpectedRevision),cancellationToken);
        if(result is null || result.Error is not null && result.Data is not null)return output.WithError("persistence_unavailable");
        if(result.Error is not null)return output.WithError(PermanentlyDeleteRecordMessages.SafeError(result.Error));
        var data=result.Data;
        if(data is null || data.RecordId!=command.RecordId
            || data.DeletedAt.Offset!=TimeSpan.Zero || data.DeletedAt.Ticks<TimeSpan.TicksPerMicrosecond
            || data.DeletedAt.Ticks%TimeSpan.TicksPerMicrosecond!=0)return output.WithError("persistence_unavailable");
        return output.WithData(new PermanentlyDeleteRecordOutput{RecordId=data.RecordId,DeletedAt=data.DeletedAt})
            .WithMessage("record_permanently_deleted");
    }
}
