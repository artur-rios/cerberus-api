using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
namespace ArturRios.Cerberus.Query.Profiles;
public sealed class GetProfileHandler(IProfileReadStore store) : IQueryHandlerAsync<GetProfileQuery, ProfileDetailsOutput>
{
    public async Task<DataOutput<ProfileDetailsOutput?>> HandleAsync(GetProfileQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<ProfileDetailsOutput?>.New;
        if (query.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (query.Access is null) return output.WithError("vault_access_required");
        if (query.ProfileId == Guid.Empty || !OpaqueAccessHandle.TryHash(query.Access, out var verifier)) return output.WithError("validation_failed");
        var result = await store.ReadAsync(new(query.Actor, verifier, query.ProfileId), cancellationToken);
        if (result.Error is not null) return output.WithError(result.Error);
        var data = result.Data;
        if (data?.Profile is null || data.Profile.ProfileId != query.ProfileId
            || !ValidIds(data.RecordIds) || !ValidIds(data.FolderIds) || !ValidIds(data.CollectionIds)
            || !ProfileProjection.TryRead(data.Profile, query.Actor, out var p)) return output.WithError("persistence_unavailable");
        return output.WithData(new ProfileDetailsOutput
        {
            ProfileId = p!.ProfileId, Revision = p.Revision, ServerSequence = p.ServerSequence, EditedAt = p.EditedAt,
            Envelope = p.Envelope, KeyWrappers = p.KeyWrappers,
            RecordIds = data.RecordIds, FolderIds = data.FolderIds, CollectionIds = data.CollectionIds
        }).WithMessage("profile_found");
    }
    private static bool ValidIds(Guid[]? ids) => ids is not null && ids.All(x => x != Guid.Empty) && ids.Distinct().Count() == ids.Length;
}
