using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Software;

public sealed class GrantSoftwareAccessHandler(IValidator<GrantSoftwareAccessCommand> validator,
    ISoftwareGrantStore store, IHeimdallClient heimdall)
    : ICommandHandlerAsync<GrantSoftwareAccessCommand, GrantSoftwareAccessOutput>
{
    public async Task<DataOutput<GrantSoftwareAccessOutput?>> HandleAsync(GrantSoftwareAccessCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<GrantSoftwareAccessOutput?>.New;
        if (command.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (command.VaultAccess is null) return output.WithError("vault_access_required");
        if (!OpaqueAccessHandle.TryHash(command.VaultAccess, out var verifier)
            || !(await validator.ValidateAsync(command, cancellationToken)).IsValid)
            return output.WithError("validation_failed");

        var identity = await heimdall.VerifyApplicationAsync(command.SoftwareIdentityId, cancellationToken);
        if (identity == HeimdallApplicationStatus.NotFound) return output.WithError("not_found");
        if (identity != HeimdallApplicationStatus.Active) return output.WithError("identity_unavailable");

        var result = await store.GrantAsync(new(command.Actor, verifier, command.ToInput()), cancellationToken);
        if (result is null || result.Error is not null && result.Data is not null)
            return output.WithError("persistence_unavailable");
        if (result.Error is not null) return output.WithError(SoftwareGrantMessages.SafeError(result.Error));

        var data = result.Data;
        if (data is null || data.GrantId != command.GrantId || data.Revision != (command.ExpectedRevision ?? 0) + 1
            || data.Revision is <= 0 or > ProtocolBinary.MaxInteger)
            return output.WithError("persistence_unavailable");

        return output.WithData(new GrantSoftwareAccessOutput { GrantId = data.GrantId, Revision = data.Revision })
            .WithMessage("software_access_granted");
    }
}
