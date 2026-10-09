using FluentValidation;

namespace ArturRios.Cerberus.Command.Collections;

public sealed class CreateCollectionValidator : AbstractValidator<CreateCollectionCommand>
{
    public CreateCollectionValidator() => RuleFor(x => x).Must(x => x.ToInput().IsValid()).WithMessage("validation_failed");
}
