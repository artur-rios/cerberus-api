using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Identity;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Identity;

public sealed class UpdateIdentityHandler(IValidator<UpdateIdentityCommand> validator, IIdentityUpdateStore store,
    IHeimdallClient identity) : ICommandHandlerAsync<UpdateIdentityCommand, UpdateIdentityOutput>
{
    public async Task<DataOutput<UpdateIdentityOutput?>> HandleAsync(UpdateIdentityCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<UpdateIdentityOutput?>.New;
        if (command.IdentityId == Guid.Empty || string.IsNullOrWhiteSpace(command.Token)) return output.WithError("authentication_required");
        if (command.VaultAccess is null) return output.WithError("vault_access_required");
        if (!OpaqueAccessHandle.TryHash(command.VaultAccess, out var verifier)
            || !(await validator.ValidateAsync(command, cancellationToken)).IsValid) return output.WithError("validation_failed");
        var result = await store.UpdateAsync(command.IdentityId, verifier,
            ct => identity.UpdateIdentityAsync(command.Token, command.IdentityId, command.Name, command.Email, ct), cancellationToken);
        if (result.Error is not null) return output.WithError(result.Error);
        var person = result.Identity;
        if (person is null || person.Id != command.IdentityId || person.Name != command.Name
            || !string.Equals(person.Email, command.Email, StringComparison.OrdinalIgnoreCase)) return output.WithError("identity_unavailable");
        return output.WithData(new UpdateIdentityOutput { Id = person.Id, Name = person.Name,
            Email = person.Email, EmailVerified = person.EmailVerified }).WithMessage("identity_updated");
    }
}
