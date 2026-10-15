using ArturRios.Cerberus.Domain.Protection;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Trash;
public sealed class RestoreTrashValidator:AbstractValidator<RestoreTrashCommand>
{
    public RestoreTrashValidator()=>RuleFor(x=>x).Must(x=>x.OperationId!=Guid.Empty
        && x.ExpectedRevision is >0 and <=ProtocolBinary.MaxInteger).WithMessage("validation_failed");
}
