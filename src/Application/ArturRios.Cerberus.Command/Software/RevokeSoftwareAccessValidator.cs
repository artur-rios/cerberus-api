using FluentValidation;

namespace ArturRios.Cerberus.Command.Software;

public sealed class RevokeSoftwareAccessValidator : AbstractValidator<RevokeSoftwareAccessCommand>
{
    public RevokeSoftwareAccessValidator() => RuleFor(x => x)
        .Must(x => x.ToInput().IsValid(x.GrantId))
        .WithMessage("validation_failed");
}
