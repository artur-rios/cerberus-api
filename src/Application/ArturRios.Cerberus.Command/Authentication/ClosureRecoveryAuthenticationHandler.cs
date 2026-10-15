using System.Globalization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Authentication;

public sealed class ClosureRecoveryAuthenticationHandler(IValidator<LoginCommand> loginValidator,
    IValidator<VerifyChallengeCommand> challengeValidator,IHeimdallClient identity,IAccountClosureRecoveryStore accounts,
    HeimdallTokenValidator tokens,TimeProvider clock)
    :ICommandHandlerAsync<ClosureRecoveryLoginCommand,ClosureRecoveryAuthenticationOutput>,
     ICommandHandlerAsync<ClosureRecoveryVerifyChallengeCommand,ClosureRecoveryAuthenticationOutput>
{
    public async Task<DataOutput<ClosureRecoveryAuthenticationOutput?>> HandleAsync(ClosureRecoveryLoginCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(!(await loginValidator.ValidateAsync(new(){Email=command.Email,Password=command.Password},cancellationToken)).IsValid)
            return DataOutput<ClosureRecoveryAuthenticationOutput?>.New.WithError("validation_failed");
        return await Complete(await identity.AuthenticateAsync(command.Email,command.Password,cancellationToken),cancellationToken);
    }
    public async Task<DataOutput<ClosureRecoveryAuthenticationOutput?>> HandleAsync(ClosureRecoveryVerifyChallengeCommand command,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(!(await challengeValidator.ValidateAsync(new(){ChallengeToken=command.ChallengeToken,Code=command.Code,RecoveryCode=command.RecoveryCode},cancellationToken)).IsValid)
            return DataOutput<ClosureRecoveryAuthenticationOutput?>.New.WithError("validation_failed");
        return await Complete(await identity.VerifyChallengeAsync(command.ChallengeToken,command.Code,command.RecoveryCode,cancellationToken),cancellationToken);
    }
    private async Task<DataOutput<ClosureRecoveryAuthenticationOutput?>> Complete(HeimdallAuthentication authentication,CancellationToken ct)
    {
        var output=DataOutput<ClosureRecoveryAuthenticationOutput?>.New;
        if(authentication.Error is{}error)return output.WithError(authentication.Login is null&&authentication.IdentityId is null
            &&error is "authentication_required" or "authentication_forbidden" or "identity_unavailable"?error:"identity_unavailable");
        var login=authentication.Login;if(login is null)return output.WithError("identity_unavailable");
        if(login.RequiresTwoFactor)
        {
            if(authentication.IdentityId is not null||login.Token is not null||login.ExpiresAt is not null||string.IsNullOrWhiteSpace(login.ChallengeToken))
                return output.WithError("identity_unavailable");
            return output.WithData(new ClosureRecoveryAuthenticationOutput{Identity=login}).WithMessage("authentication_challenge_required");
        }
        var principal=await tokens.ValidateAsync(login.Token??string.Empty);
        if(principal is null||principal.FindAll("id").Count()!=1||principal.FindAll("roleId").Count()!=1
            ||principal.FindFirst("roleId")?.Value!="3"||!Guid.TryParse(principal.FindFirst("id")?.Value,out var actor)
            ||actor==Guid.Empty||authentication.IdentityId!=actor)return output.WithError("authentication_required");
        var issued=principal.FindAll("iat").ToArray();
        if(issued.Length!=1||!long.TryParse(issued[0].Value,NumberStyles.None,CultureInfo.InvariantCulture,out var issuedAt)
            ||!FreshAuthentication.IsFresh(issuedAt,clock.GetUtcNow()))return output.WithError("fresh_authentication_required");
        if(login.ExpiresAt is null||login.ExpiresAt<=clock.GetUtcNow()||login.ChallengeToken is not null)
            return output.WithError("identity_unavailable");
        var result=await accounts.ReadAsync(new(actor,issuedAt),ct);
        if(result.Error is{}accountError)return output.WithError(result.Details is null&&accountError is
            "authentication_required" or "fresh_authentication_required" or "closure_expired" or "persistence_unavailable"?accountError:"persistence_unavailable");
        if(result.Details?.IsValid()!=true)return output.WithError("persistence_unavailable");
        return output.WithData(new ClosureRecoveryAuthenticationOutput{Identity=login,Account=result.Details}).WithMessage("closure_recovery_authenticated");
    }
}
