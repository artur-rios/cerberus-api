using FluentValidation;
namespace ArturRios.Cerberus.Command.Records;
public sealed class UpdateRecordValidator:AbstractValidator<UpdateRecordCommand>
{
    public UpdateRecordValidator()=>RuleFor(x=>x).Must(x=>x.RecordId!=Guid.Empty && x.ToInput().IsValid()).WithMessage("validation_failed");
}
