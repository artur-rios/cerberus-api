using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Accounts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RegisterAccountCommand : BaseCommand
{
    public Guid AccountId { get; set; }
    public Guid IdempotencyKey { get; set; }
    public HeimdallRegistration Identity { get; set; } = null!;
    public EncryptedEnvelope Details { get; set; } = null!;
    internal string? IdentityToken { get; private set; }

    public void SetIdentityProof(string token) => IdentityToken = token;
}
