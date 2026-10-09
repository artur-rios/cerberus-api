using FluentValidation;

namespace ArturRios.Cerberus.Command.Accounts;

public sealed class UpdateAccountValidator : AbstractValidator<UpdateAccountCommand>
{
    public UpdateAccountValidator()
    {
        RuleFor(x => x.ExpectedRevision).GreaterThan(0).WithMessage("validation_failed");
        RuleFor(x => x.Details).Must(x => x is not null && x.IsValid()).WithMessage("validation_failed");
    }
}
