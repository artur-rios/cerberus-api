using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class OpenProfileAccessOutput:CommandOutput
{
    public Guid AccountId {get;set;}
    public string VaultAccess {get;set;}=null!;
    public DateTimeOffset IssuedAt {get;set;}
    public DateTimeOffset? ExpiresAt {get;set;}
    public ProfileAccessContext Profile {get;set;}=null!;
}
