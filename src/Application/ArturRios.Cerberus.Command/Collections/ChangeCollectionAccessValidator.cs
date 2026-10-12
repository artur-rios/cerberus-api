using FluentValidation;

namespace ArturRios.Cerberus.Command.Collections;

public sealed class ChangeCollectionAccessValidator : AbstractValidator<ChangeCollectionAccessCommand>
{
    public ChangeCollectionAccessValidator() => RuleFor(x => x)
        .Must(x => x.CollectionId != Guid.Empty && x.ToInput().IsValid(x.GrantId))
        .WithMessage("validation_failed");
}
