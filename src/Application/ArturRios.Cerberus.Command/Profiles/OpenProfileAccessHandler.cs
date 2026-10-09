using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class OpenProfileAccessHandler(IValidator<OpenProfileAccessCommand> validator,IProfileAccessStore store)
    :ICommandHandlerAsync<OpenProfileAccessCommand,OpenProfileAccessOutput>
{
    public async Task<DataOutput<OpenProfileAccessOutput?>> HandleAsync(OpenProfileAccessCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();var output=DataOutput<OpenProfileAccessOutput?>.New;
        if(command.Actor==Guid.Empty)return output.WithError("authentication_required");
        if(!(await validator.ValidateAsync(command,cancellationToken)).IsValid)return output.WithError("validation_failed");
        var result=await store.OpenAsync(command.ToRequest(),cancellationToken);
        if(result?.Error is not null)return output.WithError(ProfileAccessMessages.SafeError(result.Error));
        var data=result?.Data;
        if(data?.Profile?.IsValid(data.AccountId,command.Actor,command.ProfileId,command.ExpectedRevision)!=true
            || !OpaqueAccessHandle.TryHash(data.Access,out _) || !ValidTime(data.IssuedAt)
            || data.ExpiresAt is {} expires && (!ValidTime(expires) || expires<=data.IssuedAt))
            return output.WithError("persistence_unavailable");
        return output.WithData(new OpenProfileAccessOutput{AccountId=data.AccountId,VaultAccess=data.Access,IssuedAt=data.IssuedAt,ExpiresAt=data.ExpiresAt,Profile=data.Profile}).WithMessage("profile_access_opened");
    }
    private static bool ValidTime(DateTimeOffset time)=>time>=DateTimeOffset.UnixEpoch && time.Offset==TimeSpan.Zero && time.Ticks%TimeSpan.TicksPerMicrosecond==0;
}
