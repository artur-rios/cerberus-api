using ArturRios.Cerberus.Domain.Protection;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Protection;

public sealed class ChangeVaultProtectionValidator : AbstractValidator<ChangeVaultProtectionCommand>
{
    public ChangeVaultProtectionValidator()
    {
        RuleFor(x => x).Must(x => x.ToChange().IsValid());
        RuleFor(x => x.ChallengeId).NotEmpty();
        RuleFor(x => x.Proof).Must(x => ProtocolBinary.TryDecode(x, 64, out _));
        RuleFor(x => x.RawBody).Must(x => x is { Length: > 0 and <= 1048576 });
    }
}
