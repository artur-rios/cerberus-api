using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Trash;

public sealed class RestoreTrashHandler(IValidator<RestoreTrashCommand> validator,ITrashRestoreStore store):ICommandHandlerAsync<RestoreTrashCommand,RestoreTrashOutput>
{
    public async Task<DataOutput<RestoreTrashOutput?>> HandleAsync(RestoreTrashCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();var output=DataOutput<RestoreTrashOutput?>.New;
        if(command.Actor==Guid.Empty)return output.WithError("authentication_required");
        if(command.VaultAccess is null)return output.WithError("vault_access_required");
        if(!OpaqueAccessHandle.TryHash(command.VaultAccess,out var verifier)
            ||!(await validator.ValidateAsync(command,cancellationToken)).IsValid)return output.WithError("validation_failed");
        var result=await store.RestoreAsync(new(command.Actor,verifier,command.OperationId,command.ExpectedRevision),cancellationToken);
        if(result is null || result.Error is not null && result.Data is not null)return output.WithError("persistence_unavailable");
        if(result.Error is not null)return output.WithError(RestoreTrashMessages.SafeError(result.Error));
        var d=result.Data;if(!Valid(d,command))return output.WithError("persistence_unavailable");
        return output.WithData(new RestoreTrashOutput{OperationId=d!.OperationId,RootKind=d.RootKind,RootResourceId=d.RootResourceId,
            RestoredAt=d.RestoredAt,Items=d.Items, Omissions=d.Omissions}).WithMessage("trash_restored");
    }
    private static bool Kind(string? k)=>k is "record" or "folder" or "collection" or "profile";
    private static bool Safe(long value)=>value is >0 and <=ProtocolBinary.MaxInteger;
    private static bool Time(DateTimeOffset value)=>value.Offset==TimeSpan.Zero && value.Ticks>=TimeSpan.TicksPerMicrosecond
        && value.Ticks%TimeSpan.TicksPerMicrosecond==0;
    private static bool Valid(TrashRestoreDetails? d,RestoreTrashCommand c)
    {
        if(d is null || d.OperationId!=c.OperationId || !Kind(d.RootKind) || d.RootResourceId==Guid.Empty || !Time(d.RestoredAt)
            || d.Items is null or {Count:0} || d.Omissions is null)return false;
        var ids=new HashSet<(string,Guid)>();var sequences=new HashSet<long>();
        foreach(var item in d.Items)
            if(item is null || !Kind(item.Kind) || item.ResourceId==Guid.Empty || !Safe(item.Revision) || !Safe(item.ServerSequence)
                || !Time(item.EditedAt) || !ids.Add((item.Kind,item.ResourceId)) || !sequences.Add(item.ServerSequence))return false;
        if(d.RootKind=="folder"?d.Items.Any(x=>x.Kind is not ("record" or "folder")):d.Items.Count!=1)return false;
        var root=d.Items.SingleOrDefault(x=>x.Kind==d.RootKind && x.ResourceId==d.RootResourceId);
        if(root is null || root.Revision!=c.ExpectedRevision+1)return false;
        var omissions=new HashSet<(string,Guid,string)>();
        foreach(var omission in d.Omissions)
            if(omission is null || !Kind(omission.ResourceKind) || !ids.Contains((omission.ResourceKind,omission.ResourceId))
                || omission.AssociationKind is not ("parent" or "profile" or "collection" or "record" or "folder" or "resource")
                || omission.Count<=0 || !omissions.Add((omission.ResourceKind,omission.ResourceId,omission.AssociationKind)))return false;
        return true;
    }
}
