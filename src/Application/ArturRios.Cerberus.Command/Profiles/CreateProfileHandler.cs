using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class CreateProfileHandler(IValidator<CreateProfileCommand> validator,IProfileCreateStore store):ICommandHandlerAsync<CreateProfileCommand,CreateProfileOutput>
{
    public async Task<DataOutput<CreateProfileOutput?>> HandleAsync(CreateProfileCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();var output=DataOutput<CreateProfileOutput?>.New;
        if(command.Actor==Guid.Empty)return output.WithError("authentication_required");
        if(command.VaultAccess is null)return output.WithError("vault_access_required");
        if(!OpaqueAccessHandle.TryHash(command.VaultAccess,out var verifier) || !(await validator.ValidateAsync(command,cancellationToken)).IsValid)return output.WithError("validation_failed");
        var result=await store.CreateAsync(new(command.Actor,verifier,command.ToInput()),cancellationToken);
        if(result.Error is not null)return output.WithError(result.Error);
        var data=result.Data;
        // PostgreSQL preserves microseconds; a 100ns JSON tick is not storage precision.
        if(data is null || data.ProfileId!=command.ProfileId || data.Revision!=1 || data.ServerSequence is <=0 or >ProtocolBinary.MaxInteger
            || data.EditedAt.Offset!=TimeSpan.Zero || data.EditedAt.Ticks/10!=command.EditedAt.Ticks/10)return output.WithError("persistence_unavailable");
        return output.WithData(new CreateProfileOutput{ProfileId=data.ProfileId,Revision=data.Revision,ServerSequence=data.ServerSequence,EditedAt=data.EditedAt}).WithMessage("profile_created");
    }
}
