using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Authentication;

public sealed class AuthenticationOutput : CommandOutput
{
    public required HeimdallLogin Identity { get; init; }
    public AuthenticationAccount? Account { get; init; }
}
