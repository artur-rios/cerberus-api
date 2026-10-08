using FluentValidation;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class UpdateProfileValidator:AbstractValidator<UpdateProfileCommand>
{
    public UpdateProfileValidator()=>RuleFor(x=>x).Must(x=>x.ProfileId!=Guid.Empty && x.ToInput().IsValid()).WithMessage("validation_failed");
}
