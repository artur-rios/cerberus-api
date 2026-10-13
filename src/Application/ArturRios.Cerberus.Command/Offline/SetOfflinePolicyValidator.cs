using ArturRios.Cerberus.Domain.Protection;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Offline;

public sealed class SetOfflinePolicyValidator : AbstractValidator<SetOfflinePolicyCommand>
{
    public SetOfflinePolicyValidator()
    {
        RuleFor(x => x.ExpectedPolicyRevision).InclusiveBetween(1, ProtocolBinary.MaxInteger).WithMessage("validation_failed");
        RuleFor(x => x).Must(x => x.RenewalEnabled
            ? x.RenewalInterval is { } interval && interval > TimeSpan.Zero && interval.Ticks % TimeSpan.TicksPerSecond == 0
            : x.RenewalInterval is null).WithMessage("validation_failed");
    }
}
