using ArturRios.Cerberus.Domain.Protection;
using FluentValidation;
namespace ArturRios.Cerberus.Command.Records;
public sealed class PermanentlyDeleteRecordValidator:AbstractValidator<PermanentlyDeleteRecordCommand>
{
    public PermanentlyDeleteRecordValidator()=>RuleFor(x=>x).Must(x=>x.RecordId!=Guid.Empty
        && x.ExpectedRevision is >0 and <=ProtocolBinary.MaxInteger).WithMessage("validation_failed");
}
