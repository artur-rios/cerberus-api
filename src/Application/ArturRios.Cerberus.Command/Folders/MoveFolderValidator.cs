using ArturRios.Cerberus.Domain.Protection;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Folders;
public sealed class MoveFolderValidator:AbstractValidator<MoveFolderCommand>
{
    public MoveFolderValidator()=>RuleFor(x=>x).Must(x=>x.FolderId!=Guid.Empty && x.ParentFolderId!=Guid.Empty && x.ParentFolderId!=x.FolderId
        && x.ExpectedRevision is >0 and <=ProtocolBinary.MaxInteger).WithMessage("validation_failed");
}
