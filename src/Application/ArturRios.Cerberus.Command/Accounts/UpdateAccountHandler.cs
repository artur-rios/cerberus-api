using System.Text.Json;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Accounts;

public sealed class UpdateAccountHandler(IValidator<UpdateAccountCommand> validator, IAccountUpdateStore store,
    TimeProvider clock) : ICommandHandlerAsync<UpdateAccountCommand, UpdateAccountOutput>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DataOutput<UpdateAccountOutput?>> HandleAsync(UpdateAccountCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<UpdateAccountOutput?>.New;
        if (command.IdentityId == Guid.Empty) return output.WithError("authentication_required");
        if (command.VaultAccess is null) return output.WithError("vault_access_required");
        if (!OpaqueAccessHandle.TryHash(command.VaultAccess, out var verifier)) return output.WithError("validation_failed");
        if (!(await validator.ValidateAsync(command, cancellationToken)).IsValid) return output.WithError("validation_failed");
        var result = await store.UpdateAsync(new(command.IdentityId, verifier, command.ExpectedRevision,
            JsonSerializer.SerializeToUtf8Bytes(command.Details, Json), clock.GetUtcNow()), cancellationToken);
        if (result.Error is not null) return output.WithError(result.Error);
        if (result.Id is null || result.Id == Guid.Empty || command.ExpectedRevision == long.MaxValue
            || result.Revision != command.ExpectedRevision + 1) return output.WithError("persistence_unavailable");
        return output.WithData(new UpdateAccountOutput { Id = result.Id.Value, Revision = result.Revision }).WithMessage("account_updated");
    }
}
