using FluentValidation;

namespace ArturRios.Cerberus.Command.Authentication;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("validation_failed");
        RuleFor(x => x.Password).NotEmpty().WithMessage("validation_failed");
    }
}

public sealed class VerifyChallengeValidator : AbstractValidator<VerifyChallengeCommand>
{
    public VerifyChallengeValidator()
    {
        RuleFor(x => x.ChallengeToken).NotEmpty().WithMessage("validation_failed");
        RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.Code) ^ !string.IsNullOrWhiteSpace(x.RecoveryCode))
            .WithMessage("validation_failed");
    }
}
