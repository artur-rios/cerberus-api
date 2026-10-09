using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
namespace ArturRios.Cerberus.Query.Folders;

public sealed class GetFolderHandler(IFolderReadStore store) : IQueryHandlerAsync<GetFolderQuery, FolderDetailsOutput>
{
    public async Task<DataOutput<FolderDetailsOutput?>> HandleAsync(GetFolderQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<FolderDetailsOutput?>.New;
        if (query.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (query.Access is null) return output.WithError("vault_access_required");
        if (query.FolderId == Guid.Empty || !OpaqueAccessHandle.TryHash(query.Access, out var verifier))
            return output.WithError("validation_failed");
        var result = await store.ReadAsync(new(query.Actor, verifier, query.FolderId), cancellationToken);
        if (result?.Error is not null)
            return output.WithError(result.Data is not null ? "persistence_unavailable" : FolderReadMessages.SafeError(result.Error));
        var data = result?.Data;
        if (data?.Folder is null || data.Folder.FolderId != query.FolderId
            || !ValidIds(data.ProfileIds) || !ValidIds(data.CollectionIds) || (data.ParentFolderId == Guid.Empty || data.ParentFolderId == data.Folder.FolderId)
            || !FolderProjection.TryRead(data.Folder, out var folder))
            return output.WithError("persistence_unavailable");
        return output.WithData(new FolderDetailsOutput
        {
            FolderId = folder!.FolderId, Revision = folder.Revision, ServerSequence = folder.ServerSequence,
            EditedAt = folder.EditedAt, Envelope = folder.Envelope,
            ProfileIds = data.ProfileIds.Order().ToArray(), ParentFolderId = data.ParentFolderId, CollectionIds = data.CollectionIds.Order().ToArray()
        }).WithMessage("folder_found");
    }
    private static bool ValidIds(Guid[]? ids) => ids is not null && ids.All(x => x != Guid.Empty) && ids.Distinct().Count() == ids.Length;
}
