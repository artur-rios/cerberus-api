using ArturRios.Cerberus.Domain.Protection;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Trash;
public sealed class EmptyTrashValidator:AbstractValidator<EmptyTrashCommand>
{
    public EmptyTrashValidator()=>RuleFor(x=>x.ExpectedRevision).InclusiveBetween(1,ProtocolBinary.MaxInteger).WithMessage("validation_failed");
}
