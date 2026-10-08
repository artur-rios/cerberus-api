using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class CreateProfileOutput:CommandOutput
{
    public Guid ProfileId {get;set;}
    public long Revision {get;set;}
    public long ServerSequence {get;set;}
    public DateTimeOffset EditedAt {get;set;}
}
