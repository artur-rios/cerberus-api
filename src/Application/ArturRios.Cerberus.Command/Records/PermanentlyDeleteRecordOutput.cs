using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Records;
public sealed class PermanentlyDeleteRecordOutput:CommandOutput
{
    public Guid RecordId {get;set;}
    public DateTimeOffset DeletedAt {get;set;}
}
