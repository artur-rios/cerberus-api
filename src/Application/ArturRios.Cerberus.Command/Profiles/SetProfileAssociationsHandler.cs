using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class SetProfileAssociationsHandler(IValidator<SetProfileAssociationsCommand> validator,IProfileAssociationStore store):ICommandHandlerAsync<SetProfileAssociationsCommand,SetProfileAssociationsOutput>
{
    public async Task<DataOutput<SetProfileAssociationsOutput?>> HandleAsync(SetProfileAssociationsCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();var output=DataOutput<SetProfileAssociationsOutput?>.New;
        if(command.Actor==Guid.Empty)return output.WithError("authentication_required");
        if(command.VaultAccess is null)return output.WithError("vault_access_required");
        if(!OpaqueAccessHandle.TryHash(command.VaultAccess,out var verifier) || !(await validator.ValidateAsync(command,cancellationToken)).IsValid)return output.WithError("validation_failed");
        var result=await store.SetAsync(new(command.Actor,verifier,command.ProfileId,command.ToInput()),cancellationToken);
        if(result.Error is not null)return output.WithError(result.Error);
        var data=result.Data;
        if(data is null || data.ProfileId!=command.ProfileId || data.Revision!=command.ExpectedRevision+1 || data.Revision>ProtocolBinary.MaxInteger
            || data.ServerSequence is <=0 or >ProtocolBinary.MaxInteger || !SameSet(data.RecordIds,command.RecordIds)
            || !SameSet(data.FolderIds,command.FolderIds) || !SameSet(data.CollectionIds,command.CollectionIds))return output.WithError("persistence_unavailable");
        return output.WithData(new SetProfileAssociationsOutput{ProfileId=data.ProfileId,Revision=data.Revision,ServerSequence=data.ServerSequence,
            RecordIds=data.RecordIds.Order().ToArray(),FolderIds=data.FolderIds.Order().ToArray(),CollectionIds=data.CollectionIds.Order().ToArray()}).WithMessage("profile_associations_set");
    }
    private static bool SameSet(Guid[]? actual,Guid[] expected)=>ProfileAssociationInput.ValidIds(actual) && actual!.Order().SequenceEqual(expected.Order());
}
