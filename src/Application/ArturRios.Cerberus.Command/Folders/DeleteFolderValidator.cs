using ArturRios.Cerberus.Domain.Protection;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Folders;
public sealed class DeleteFolderValidator:AbstractValidator<DeleteFolderCommand>
{
    public DeleteFolderValidator()=>RuleFor(x=>x).Must(x=>x.FolderId!=Guid.Empty
        && x.ExpectedRevision is >0 and <=ProtocolBinary.MaxInteger).WithMessage("validation_failed");
}
