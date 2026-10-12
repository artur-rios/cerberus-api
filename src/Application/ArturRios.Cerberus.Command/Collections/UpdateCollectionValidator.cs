using FluentValidation;

namespace ArturRios.Cerberus.Command.Collections;

public sealed class UpdateCollectionValidator : AbstractValidator<UpdateCollectionCommand>
{
    public UpdateCollectionValidator() => RuleFor(x => x)
        .Must(x => x.CollectionId != Guid.Empty && x.ToInput().IsValid())
        .WithMessage("validation_failed");
}
