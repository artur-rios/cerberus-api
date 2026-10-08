using FluentValidation;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class CreateProfileValidator:AbstractValidator<CreateProfileCommand>
{
    public CreateProfileValidator()=>RuleFor(x=>x).Must(x=>x.ToInput().IsValid()).WithMessage("validation_failed");
}
