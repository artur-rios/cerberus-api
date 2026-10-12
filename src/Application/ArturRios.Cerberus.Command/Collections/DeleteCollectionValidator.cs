using ArturRios.Cerberus.Domain.Protection;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Collections;

public sealed class DeleteCollectionValidator : AbstractValidator<DeleteCollectionCommand>
{
    public DeleteCollectionValidator() => RuleFor(x => x).Must(x => x.CollectionId != Guid.Empty
        && x.ExpectedRevision is > 0 and <= ProtocolBinary.MaxInteger).WithMessage("validation_failed");
}
