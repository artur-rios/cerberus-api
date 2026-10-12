using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Cerberus.Command.Collections;

public sealed class SetCollectionMembershipHandler(IValidator<SetCollectionMembershipCommand> validator, ICollectionMembershipStore store)
    : ICommandHandlerAsync<SetCollectionMembershipCommand, SetCollectionMembershipOutput>
{
    public async Task<DataOutput<SetCollectionMembershipOutput?>> HandleAsync(SetCollectionMembershipCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<SetCollectionMembershipOutput?>.New;
        if (command.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (command.VaultAccess is null) return output.WithError("vault_access_required");
        if (!OpaqueAccessHandle.TryHash(command.VaultAccess, out var verifier)
            || !(await validator.ValidateAsync(command, cancellationToken)).IsValid)
            return output.WithError("validation_failed");

        var result = await store.SetAsync(
            new(command.Actor, verifier, command.CollectionId, command.ToInput()), cancellationToken);
        if (result is null || result.Error is not null && result.Data is not null)
            return output.WithError("persistence_unavailable");
        if (result.Error is not null) return output.WithError(SetCollectionMembershipMessages.SafeError(result.Error));

        var data = result.Data;
        var edited = command.EditedAt.AddTicks(-(command.EditedAt.Ticks % TimeSpan.TicksPerMicrosecond));
        if (data is null || data.CollectionId != command.CollectionId || data.Revision != command.ExpectedRevision + 1
            || data.Revision is <= 0 or > ProtocolBinary.MaxInteger
            || data.ServerSequence is <= 0 or > ProtocolBinary.MaxInteger
            || data.EditedAt.Offset != TimeSpan.Zero || data.EditedAt.Ticks < TimeSpan.TicksPerMicrosecond
            || data.EditedAt.Ticks % TimeSpan.TicksPerMicrosecond != 0 || data.EditedAt != edited
            || !SameSet(data.RecordIds, command.RecordIds) || !SameSet(data.FolderIds, command.FolderIds))
            return output.WithError("persistence_unavailable");

        return output.WithData(new SetCollectionMembershipOutput
        {
            CollectionId = data.CollectionId,
            Revision = data.Revision,
            ServerSequence = data.ServerSequence,
            EditedAt = data.EditedAt,
            RecordIds = data.RecordIds.Order().ToArray(),
            FolderIds = data.FolderIds.Order().ToArray()
        }).WithMessage("collection_membership_set");
    }

    private static bool SameSet(Guid[]? actual, Guid[] expected) => CollectionMembershipInput.ValidIds(actual)
        && actual!.Order().SequenceEqual(expected.Order());
}
