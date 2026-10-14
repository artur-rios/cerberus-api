using ArturRios.Cerberus.Domain.Protection;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Offline;

public sealed class RenewOfflineAuthorizationValidator : AbstractValidator<RenewOfflineAuthorizationCommand>
{
    public RenewOfflineAuthorizationValidator()
    {
        RuleFor(c => c.ProfileId).NotEmpty();
        RuleFor(c => c.ExpectedProfileRevision).InclusiveBetween(1, ProtocolBinary.MaxInteger);
    }
}
