using ArturRios.Cerberus.Domain.Protection;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Protection;

public sealed class RecoverVaultValidator : AbstractValidator<RecoverVaultCommand>
{
    public RecoverVaultValidator()
    {
        RuleFor(x => x).Must(x => x.ToReplacement().IsValid());
        RuleFor(x => x.IdentityIssuedAt).InclusiveBetween(0, ProtocolBinary.MaxInteger);
        RuleFor(x => x.ChallengeId).NotEmpty();
        RuleFor(x => x.Proof).Must(x => ProtocolBinary.TryDecode(x, 64, out _));
        RuleFor(x => x.RawBody).Must(x => x is { Length: > 0 and <= 1048576 });
    }
}
