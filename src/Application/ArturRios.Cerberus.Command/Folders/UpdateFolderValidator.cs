using FluentValidation;
namespace ArturRios.Cerberus.Command.Folders;
public sealed class UpdateFolderValidator:AbstractValidator<UpdateFolderCommand>
{
    public UpdateFolderValidator()=>RuleFor(x=>x).Must(x=>x.FolderId!=Guid.Empty && x.ToInput().IsValid()).WithMessage("validation_failed");
}
