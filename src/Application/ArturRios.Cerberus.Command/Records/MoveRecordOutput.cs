using System.Text.Json.Serialization;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Records;
public sealed class MoveRecordOutput:CommandOutput
{
    public Guid RecordId {get;set;}
    [JsonIgnore(Condition=JsonIgnoreCondition.Never)] public Guid? FolderId {get;set;}
    public long Revision {get;set;}
    public long ServerSequence {get;set;}
}
