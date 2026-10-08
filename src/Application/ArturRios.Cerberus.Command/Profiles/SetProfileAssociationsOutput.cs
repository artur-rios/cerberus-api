using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class SetProfileAssociationsOutput:CommandOutput
{
    public Guid ProfileId {get;set;}
    public long Revision {get;set;}
    public long ServerSequence {get;set;}
    public Guid[] RecordIds {get;set;}=[];
    public Guid[] FolderIds {get;set;}=[];
    public Guid[] CollectionIds {get;set;}=[];
}
