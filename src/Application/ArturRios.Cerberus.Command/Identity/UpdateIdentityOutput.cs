using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Identity;

public sealed class UpdateIdentityOutput : CommandOutput
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string Email { get; set; } = null!;
    public bool EmailVerified { get; set; }
}
