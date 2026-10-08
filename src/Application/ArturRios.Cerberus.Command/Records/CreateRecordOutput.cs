using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Records;

public sealed class CreateRecordOutput : CommandOutput
{
    public Guid RecordId { get; set; }
    public long Revision { get; set; }
    public long ServerSequence { get; set; }
    public DateTimeOffset EditedAt { get; set; }
}
