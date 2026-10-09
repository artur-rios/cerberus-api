using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;

namespace ArturRios.Cerberus.Query.Accounts;

public sealed class GetAccountHandler(IAccountReadStore accounts, TimeProvider clock) : IQueryHandlerAsync<GetAccountQuery, AccountOutput>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict };

    public async Task<DataOutput<AccountOutput?>> HandleAsync(GetAccountQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<AccountOutput?>.New;
        if (query.IdentityId == Guid.Empty) return output.WithError("authentication_required");
        if (query.VaultAccess is null) return output.WithError("vault_access_required");
        if (!OpaqueAccessHandle.TryHash(query.VaultAccess, out var verifier)) return output.WithError("validation_failed");
        var result = await accounts.ReadAsync(query.IdentityId, verifier, clock.GetUtcNow(), cancellationToken);
        if (result.Error is not null) return output.WithError(result.Error);
        if (result.Account is null) return output.WithError("persistence_unavailable");
        EncryptedEnvelope? details;
        try { details = JsonSerializer.Deserialize<EncryptedEnvelope>(result.Account.DetailsEnvelope, Json); }
        catch (JsonException) { return output.WithError("persistence_unavailable"); }
        if (details?.IsValid() != true) return output.WithError("persistence_unavailable");
        return output.WithData(new AccountOutput
        {
            Id = result.Account.Id, Revision = result.Account.Revision,
            State = result.Account.State.ToString().ToLowerInvariant(), Details = details
        }).WithMessage("account_found");
    }
}
