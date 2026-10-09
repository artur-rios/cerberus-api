using FluentValidation;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class SetProfileAssociationsValidator:AbstractValidator<SetProfileAssociationsCommand>
{
    public SetProfileAssociationsValidator()=>RuleFor(x=>x).Must(x=>x.ProfileId!=Guid.Empty && x.ToInput().IsValid()).WithMessage("validation_failed");
}
