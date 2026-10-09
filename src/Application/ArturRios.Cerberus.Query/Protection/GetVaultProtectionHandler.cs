using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Query;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;

namespace ArturRios.Cerberus.Query.Protection;

public sealed class GetVaultProtectionQuery(Guid actor) : BaseQuery
{
    public Guid Actor { get; } = actor;
}
public sealed class VaultProtectionOutput : QueryOutput
{
    public Guid AccountId { get; init; }
    public long ProtectionRevision { get; init; }
    public long KeyEpoch { get; init; }
    public long RecoveryGeneration { get; init; }
    public required ProtectionMaterial Material { get; init; }
}
public sealed class GetVaultProtectionHandler(IVaultProtectionStore store) : IQueryHandlerAsync<GetVaultProtectionQuery, VaultProtectionOutput>
{
    public async Task<DataOutput<VaultProtectionOutput?>> HandleAsync(GetVaultProtectionQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();var output = DataOutput<VaultProtectionOutput?>.New;
        if (query.Actor == Guid.Empty) return output.WithError("authentication_required");
        var result = await store.ReadAsync(query.Actor,cancellationToken);
        if (result.Error is not null) return output.WithError(result.Error);
        var data = result.Data;
        if (data is null || data.AccountId == Guid.Empty || data.Material?.IsValid() != true
            || data.ProtectionRevision is <= 0 or > ProtocolBinary.MaxInteger || data.KeyEpoch != data.Material.PasswordWrapper.KeyEpoch
            || data.RecoveryGeneration != data.Material.RecoveryWrapper.Generation) return output.WithError("persistence_unavailable");
        return output.WithData(new VaultProtectionOutput { AccountId = data.AccountId,ProtectionRevision = data.ProtectionRevision,
            KeyEpoch = data.KeyEpoch,RecoveryGeneration = data.RecoveryGeneration,Material = data.Material }).WithMessage("protection_found");
    }
}
public static class VaultProtectionQueryMessages
{
    public static readonly IReadOnlyDictionary<string,int> StatusCodes = new Dictionary<string,int>
    { ["protection_found"] = 200,["authentication_required"] = 401,["validation_failed"] = 400,["not_found"] = 404,["persistence_unavailable"] = 503 };
}
