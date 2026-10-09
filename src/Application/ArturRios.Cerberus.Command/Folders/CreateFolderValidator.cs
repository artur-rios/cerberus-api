using FluentValidation;

namespace ArturRios.Cerberus.Command.Folders;

public sealed class CreateFolderValidator : AbstractValidator<CreateFolderCommand>
{
    public CreateFolderValidator() => RuleFor(x => x).Must(x => x.ToInput().IsValid()).WithMessage("validation_failed");
}
