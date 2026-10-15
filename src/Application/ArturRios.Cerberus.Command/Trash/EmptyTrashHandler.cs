using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Trash;

public sealed class EmptyTrashHandler(IValidator<EmptyTrashCommand> validator,ITrashEmptyStore store):ICommandHandlerAsync<EmptyTrashCommand,EmptyTrashOutput>
{
    public async Task<DataOutput<EmptyTrashOutput?>> HandleAsync(EmptyTrashCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();var output=DataOutput<EmptyTrashOutput?>.New;
        if(command.Actor==Guid.Empty)return output.WithError("authentication_required");
        if(command.VaultAccess is null)return output.WithError("vault_access_required");
        if(!OpaqueAccessHandle.TryHash(command.VaultAccess,out var verifier)||!(await validator.ValidateAsync(command,cancellationToken)).IsValid)return output.WithError("validation_failed");
        var result=await store.EmptyAsync(new(command.Actor,verifier,command.ExpectedRevision),cancellationToken);
        if(result is null||result.Error is not null&&result.Data is not null)return output.WithError("persistence_unavailable");
        if(result.Error is not null)return output.WithError(EmptyTrashMessages.SafeError(result.Error));
        var d=result.Data;if(!Valid(d))return output.WithError("persistence_unavailable");
        return output.WithData(new EmptyTrashOutput{AccountId=d!.AccountId,PurgedAt=d.PurgedAt,Counts=d.Counts.OrderBy(x=>x.Kind,StringComparer.Ordinal).ToArray(),TotalPurged=d.TotalPurged}).WithMessage("trash_emptied");
    }
    private static bool Valid(TrashEmptyDetails? d)
    {
        if(d is null||d.AccountId==Guid.Empty||d.PurgedAt.Offset!=TimeSpan.Zero||d.PurgedAt<DateTimeOffset.UnixEpoch||d.PurgedAt.Ticks%TimeSpan.TicksPerMicrosecond!=0
            ||d.Counts is null or {Count:not 4}||d.TotalPurged is <0 or >ProtocolBinary.MaxInteger)return false;
        var kinds=new HashSet<string>(StringComparer.Ordinal);long total=0;
        foreach(var row in d.Counts)
        {
            if(row is null||row.Kind is not("record" or "folder" or "collection" or "profile")||!kinds.Add(row.Kind)||row.Count is <0 or >ProtocolBinary.MaxInteger)return false;
            total+=row.Count;
        }
        return total==d.TotalPurged;
    }
}
