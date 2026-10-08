using ArturRios.Cerberus.Domain.Protection;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Protection;

public sealed class InitializeVaultValidator : AbstractValidator<InitializeVaultCommand>
{
    public InitializeVaultValidator()
    {
        RuleFor(x => x.AccountId).NotEmpty();
        RuleFor(x => x.ExpectedAccountRevision).InclusiveBetween(1, ProtocolBinary.MaxInteger);
        RuleFor(x => x.Material).Must(x => x?.IsValid() == true && x.PasswordWrapper.KeyEpoch == 1 && x.RecoveryWrapper.Generation == 1);
    }
}
public sealed class IssueVaultChallengeValidator : AbstractValidator<IssueVaultChallengeCommand>
{
    public IssueVaultChallengeValidator()
    {
        RuleFor(x => x.Operation).Must(x => x is "unlock-account" or "change-protection");
        RuleFor(x => x.RequestHash).Must(x => ProtocolBinary.TryDecode(x, 32, out _));
    }
}
public sealed class UnlockVaultValidator : AbstractValidator<UnlockVaultCommand>
{
    public UnlockVaultValidator()
    {
        RuleFor(x => x.ExpectedProtectionRevision).InclusiveBetween(1, ProtocolBinary.MaxInteger);
        RuleFor(x => x.ChallengeId).NotEmpty();
        RuleFor(x => x.Proof).Must(x => ProtocolBinary.TryDecode(x, 64, out _));
        RuleFor(x => x.RawBody).Must(x => x is { Length: > 0 and <= 1048576 });
    }
}
