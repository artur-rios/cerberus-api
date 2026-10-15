using ArturRios.Cerberus.Domain.Software;

namespace ArturRios.Cerberus.Shared.Identity;

public sealed record HeimdallApplicationAuthentication(SoftwareIdentity? Identity=null,string? Error=null);
public interface IHeimdallApplicationClient
{
    Task<HeimdallApplicationAuthentication> AuthenticateAsync(string token,CancellationToken cancellationToken);
}
