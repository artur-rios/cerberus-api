using FluentValidation;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class IssueProfileAccessChallengeValidator:AbstractValidator<IssueProfileAccessChallengeCommand>
{
    public IssueProfileAccessChallengeValidator()=>RuleFor(x=>x).Must(x=>x.ToRequest().IsValid()).WithMessage("validation_failed");
}
