using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;

namespace ArturRios.Cerberus.Command.Accounts;

public sealed class RequestAccountClosureHandler(IAccountClosureStore store, TimeProvider clock)
    : ICommandHandlerAsync<RequestAccountClosureCommand, AccountClosureOutput>
{
    public async Task<DataOutput<AccountClosureOutput?>> HandleAsync(RequestAccountClosureCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var output = DataOutput<AccountClosureOutput?>.New;
        if (command.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (!FreshAuthentication.IsFresh(command.AuthenticationIssuedAt, clock.GetUtcNow())) return output.WithError("fresh_authentication_required");
        if (!new AccountClosureInput(command.ExpectedRevision).IsValid()) return output.WithError("validation_failed");
        var result = await store.RequestAsync(new(command.Actor, command.AuthenticationIssuedAt, command.ExpectedRevision), cancellationToken);
        if (result.Error is { } error) return output.WithError(result.Details is null && error is
            "validation_failed" or "authentication_required" or "fresh_authentication_required" or "not_found" or "revision_conflict" or "persistence_unavailable"
                ? error : "persistence_unavailable");
        var d = result.Details;
        if (d?.IsValid() != true || !d.AlreadyPending && d.Revision != command.ExpectedRevision + 1)
            return output.WithError("persistence_unavailable");
        return output.WithData(new AccountClosureOutput { AccountId = d.AccountId, Revision = d.Revision,
            RevocationGeneration = d.RevocationGeneration, ClosureRequestedAt = d.ClosureRequestedAt,
            ClosureDeadline = d.ClosureDeadline, ServerTime = d.ServerTime, AlreadyPending = d.AlreadyPending })
            .WithMessage("account_closure_pending");
    }
}
