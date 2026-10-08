using FluentValidation;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class OpenProfileAccessValidator:AbstractValidator<OpenProfileAccessCommand>
{
    public OpenProfileAccessValidator()=>RuleFor(x=>x).Must(x=>x.ToRequest().IsValid()).WithMessage("validation_failed");
}
