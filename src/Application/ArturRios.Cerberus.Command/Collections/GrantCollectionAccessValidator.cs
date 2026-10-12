using FluentValidation;

namespace ArturRios.Cerberus.Command.Collections;

public sealed class GrantCollectionAccessValidator : AbstractValidator<GrantCollectionAccessCommand>
{
    public GrantCollectionAccessValidator() => RuleFor(x => x)
        .Must(x => x.CollectionId != Guid.Empty && x.ToInput().IsValid())
        .WithMessage("validation_failed");
}
