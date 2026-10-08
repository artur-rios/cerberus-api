using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Authentication;

public sealed class AuthenticationHandler(IValidator<LoginCommand> loginValidator,
    IValidator<VerifyChallengeCommand> challengeValidator, IHeimdallClient identity, IAccountAuthenticationStore accounts)
    : ICommandHandlerAsync<LoginCommand, AuthenticationOutput>, ICommandHandlerAsync<VerifyChallengeCommand, AuthenticationOutput>
{
    public async Task<DataOutput<AuthenticationOutput?>> HandleAsync(LoginCommand command, CancellationToken cancellationToken = default)
    {
        if (!(await loginValidator.ValidateAsync(command, cancellationToken)).IsValid)
            return DataOutput<AuthenticationOutput?>.New.WithError("validation_failed");
        return await CompleteAsync(await identity.AuthenticateAsync(command.Email, command.Password, cancellationToken), cancellationToken);
    }

    public async Task<DataOutput<AuthenticationOutput?>> HandleAsync(VerifyChallengeCommand command, CancellationToken cancellationToken = default)
    {
        if (!(await challengeValidator.ValidateAsync(command, cancellationToken)).IsValid)
            return DataOutput<AuthenticationOutput?>.New.WithError("validation_failed");
        return await CompleteAsync(await identity.VerifyChallengeAsync(command.ChallengeToken, command.Code, command.RecoveryCode, cancellationToken), cancellationToken);
    }

    private async Task<DataOutput<AuthenticationOutput?>> CompleteAsync(HeimdallAuthentication authentication, CancellationToken cancellationToken)
    {
        var output = DataOutput<AuthenticationOutput?>.New;
        if (authentication.Error is not null) return output.WithError(authentication.Error);
        if (authentication.Login is null) return output.WithError("identity_unavailable");
        if (authentication.Login.RequiresTwoFactor)
            return output.WithData(new AuthenticationOutput { Identity = authentication.Login }).WithMessage("authentication_challenge_required");
        if (authentication.IdentityId is null) return output.WithError("identity_unavailable");
        var account = await accounts.FindAsync(authentication.IdentityId.Value, cancellationToken);
        if (account.Error is not null) return output.WithError(account.Error);
        return output.WithData(new AuthenticationOutput { Identity = authentication.Login, Account = account.Account }).WithMessage("authenticated");
    }
}
