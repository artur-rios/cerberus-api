using FluentValidation;

namespace ArturRios.Cerberus.Command.Software;

public sealed class GrantSoftwareAccessValidator : AbstractValidator<GrantSoftwareAccessCommand>
{
    public GrantSoftwareAccessValidator() => RuleFor(x => x).Must(x => x.ToInput().IsValid())
        .WithMessage("validation_failed");
}
