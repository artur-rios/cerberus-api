using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class DeleteProfileOutput : CommandOutput
{
    public Guid ProfileId { get; set; }
    public Guid TrashOperationId { get; set; }
    public long Revision { get; set; }
    public long ServerSequence { get; set; }
    public DateTimeOffset DeletedAt { get; set; }
    public DateTimeOffset PurgeAt { get; set; }
}
