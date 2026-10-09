using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Folders;
public sealed class UpdateFolderOutput:CommandOutput
{
    public Guid FolderId {get;set;}
    public long Revision {get;set;}
    public long ServerSequence {get;set;}
    public DateTimeOffset EditedAt {get;set;}
}
