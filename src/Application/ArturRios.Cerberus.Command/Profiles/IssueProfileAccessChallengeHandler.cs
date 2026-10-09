using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class IssueProfileAccessChallengeHandler(IValidator<IssueProfileAccessChallengeCommand> validator,IProfileAccessStore store)
    :ICommandHandlerAsync<IssueProfileAccessChallengeCommand,IssueProfileAccessChallengeOutput>
{
    public async Task<DataOutput<IssueProfileAccessChallengeOutput?>> HandleAsync(IssueProfileAccessChallengeCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();var output=DataOutput<IssueProfileAccessChallengeOutput?>.New;
        if(command.Actor==Guid.Empty)return output.WithError("authentication_required");
        if(!(await validator.ValidateAsync(command,cancellationToken)).IsValid)return output.WithError("validation_failed");
        var result=await store.ChallengeAsync(command.ToRequest(),cancellationToken);
        if(result?.Error is not null)return output.WithError(ProfileAccessMessages.SafeError(result.Error));
        var data=result?.Data;var c=data?.Challenge;var p=data?.Profile;
        if(p?.IsValid(command.Actor,command.ProfileId,command.ExpectedRevision)!=true || c is null
            || c.Format!="cerberus-challenge-v1" || c.Operation!="unlock-profile" || c.ScopeKind!="profile"
            || c.IdentityId!=command.Actor || c.AccountId!=p.AccountId || c.ScopeId!=command.ProfileId
            || c.KeyEpoch!=p.Envelope.KeyEpoch || c.ProtectionRevision!=p.Revision || c.Generation is not null
            || c.ChallengeId==Guid.Empty || !ProtocolBinary.TryDecode(c.Nonce,32,out _) || c.RequestHash!=command.RequestHash
            || c.IssuedAt is <0 or >ProtocolBinary.MaxInteger-60 || c.ExpiresAt!=c.IssuedAt+60)
            return output.WithError("persistence_unavailable");
        return output.WithData(new IssueProfileAccessChallengeOutput{Challenge=c,Profile=p}).WithMessage("profile_access_challenge_issued");
    }
}
