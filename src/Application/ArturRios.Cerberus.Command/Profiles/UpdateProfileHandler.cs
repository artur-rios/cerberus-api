using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class UpdateProfileHandler(IValidator<UpdateProfileCommand> validator,IProfileUpdateStore store):ICommandHandlerAsync<UpdateProfileCommand,UpdateProfileOutput>
{
    public async Task<DataOutput<UpdateProfileOutput?>> HandleAsync(UpdateProfileCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();var output=DataOutput<UpdateProfileOutput?>.New;
        if(command.Actor==Guid.Empty)return output.WithError("authentication_required");
        if(command.VaultAccess is null)return output.WithError("vault_access_required");
        if(!OpaqueAccessHandle.TryHash(command.VaultAccess,out var verifier) || !(await validator.ValidateAsync(command,cancellationToken)).IsValid)return output.WithError("validation_failed");
        var result=await store.UpdateAsync(new(command.Actor,verifier,command.ProfileId,command.ToInput()),cancellationToken);
        if(result.Error is not null)return output.WithError(result.Error);
        var data=result.Data;var editedAt=command.EditedAt.AddTicks(-(command.EditedAt.Ticks%TimeSpan.TicksPerMicrosecond));
        if(data is null || data.ProfileId!=command.ProfileId || data.Revision!=command.ExpectedRevision+1 || data.Revision>ProtocolBinary.MaxInteger
            || data.ServerSequence is <=0 or >ProtocolBinary.MaxInteger || data.EditedAt.Offset!=TimeSpan.Zero || data.EditedAt!=editedAt)return output.WithError("persistence_unavailable");
        return output.WithData(new UpdateProfileOutput{ProfileId=data.ProfileId,Revision=data.Revision,ServerSequence=data.ServerSequence,EditedAt=data.EditedAt}).WithMessage("profile_updated");
    }
}
