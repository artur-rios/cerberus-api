using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Accounts;

public sealed class RegisterAccountOutput : CommandOutput
{
    public Guid Id { get; set; }
    public long Revision { get; set; }
    public string OnboardingState { get; set; } = "protection_required";
    public bool Replayed { get; set; }
}
