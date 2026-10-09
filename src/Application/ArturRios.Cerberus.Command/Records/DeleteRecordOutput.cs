using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Records;
public sealed class DeleteRecordOutput:CommandOutput
{
    public Guid RecordId {get;set;}
    public Guid TrashOperationId {get;set;}
    public long Revision {get;set;}
    public long ServerSequence {get;set;}
    public DateTimeOffset DeletedAt {get;set;}
    public DateTimeOffset PurgeAt {get;set;}
}
