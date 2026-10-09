using ArturRios.Cerberus.Domain.Protection;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class DeleteProfileValidator : AbstractValidator<DeleteProfileCommand>
{
    public DeleteProfileValidator()=>RuleFor(x=>x).Must(x=>x.ProfileId!=Guid.Empty && x.ExpectedRevision is >0 and <=ProtocolBinary.MaxInteger).WithMessage("validation_failed");
}
