using FluentValidation;

namespace ArturRios.Cerberus.Command.Identity;

public sealed class UpdateIdentityValidator : AbstractValidator<UpdateIdentityCommand>
{
    public UpdateIdentityValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).WithMessage("validation_failed");
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("validation_failed");
    }
}
