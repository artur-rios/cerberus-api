using FluentValidation;

namespace ArturRios.Cerberus.Command.Records;

public sealed class CreateRecordValidator : AbstractValidator<CreateRecordCommand>
{
    public CreateRecordValidator() => RuleFor(x => x).Must(x => x.ToInput().IsValid()).WithMessage("validation_failed");
}
