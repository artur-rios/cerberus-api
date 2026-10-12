using FluentValidation;

namespace ArturRios.Cerberus.Command.Collections;

public sealed class SetCollectionMembershipValidator : AbstractValidator<SetCollectionMembershipCommand>
{
    public SetCollectionMembershipValidator() => RuleFor(x => x)
        .Must(x => x.CollectionId != Guid.Empty && x.ToInput().IsValid())
        .WithMessage("validation_failed");
}
