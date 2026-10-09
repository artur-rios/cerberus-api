using System.Text.Json.Serialization;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Folders;
public sealed class MoveFolderOutput:CommandOutput
{
    public Guid FolderId {get;set;}
    [JsonIgnore(Condition=JsonIgnoreCondition.Never)] public Guid? ParentFolderId {get;set;}
    public long Revision {get;set;}
    public long ServerSequence {get;set;}
}
