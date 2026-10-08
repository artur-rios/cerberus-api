using FluentValidation;

namespace ArturRios.Cerberus.Command.Accounts;

public sealed class RegisterAccountValidator : AbstractValidator<RegisterAccountCommand>
{
    public RegisterAccountValidator()
    {
        RuleFor(x => x.AccountId).NotEmpty().WithMessage("validation_failed");
        RuleFor(x => x.IdempotencyKey).NotEmpty().WithMessage("validation_failed");
        RuleFor(x => x.Details).Must(x => x is not null && x.IsValid()).WithMessage("validation_failed");
        RuleFor(x => x.Identity).NotNull().WithMessage("validation_failed");
        When(x => x.Identity is not null, () =>
        {
            RuleFor(x => x.Identity.Name).NotEmpty().WithMessage("validation_failed")
                .MaximumLength(200).WithMessage("validation_failed");
            RuleFor(x => x.Identity.Email).NotEmpty().WithMessage("validation_failed")
                .EmailAddress().WithMessage("validation_failed");
            RuleFor(x => x.Identity.Password).NotEmpty().WithMessage("validation_failed")
                .MinimumLength(8).WithMessage("validation_failed");
        });
    }
}
