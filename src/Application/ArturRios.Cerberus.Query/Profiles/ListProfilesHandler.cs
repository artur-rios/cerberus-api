using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
namespace ArturRios.Cerberus.Query.Profiles;

public sealed class ListProfilesHandler(IProfileListStore store, CerberusOptions options, ProfileListCursor cursors)
    : IQueryHandlerAsync<ListProfilesQuery, ProfileListOutput>
{
    public async Task<DataOutput<ProfileListOutput?>> HandleAsync(ListProfilesQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = DataOutput<ProfileListOutput?>.New;
        if (query.Actor == Guid.Empty) return output.WithError("authentication_required");
        if (query.Access is null) return output.WithError("vault_access_required");
        if (!OpaqueAccessHandle.TryHash(query.Access, out var verifier)) return output.WithError("validation_failed");
        var size = query.RequestedPageSize ?? Math.Min(50, options.MaxPageSize);
        if (size <= 0 || size > options.MaxPageSize) return output.WithError("validation_failed");
        long after = 0; long? boundary = null;
        if (query.Cursor is not null)
        {
            if (!cursors.TryDecode(query.Cursor, out var cursor) || cursor!.Actor != query.Actor
                || cursor.AccessVerifier != verifier || cursor.PageSize != size) return output.WithError("validation_failed");
            after = cursor.After; boundary = cursor.Boundary;
        }
        var result = await store.ListAsync(new(query.Actor, verifier, size, after, boundary), cancellationToken);
        if (result.Error is not null) return output.WithError(result.Error);
        var page = result.Data;
        if (page?.Items is null || page.Items.Count > size || page.Boundary < after || page.Boundary > ProtocolBinary.MaxInteger
            || boundary is not null && page.Boundary != boundary || page.HasMore && page.Items.Count != size)
            return output.WithError("persistence_unavailable");
        var items = new List<ProfileListItem>(); var ids = new HashSet<Guid>(); var previous = after;
        foreach (var row in page.Items)
        {
            if (!ProfileProjection.TryRead(row, query.Actor, out var item) || !ids.Add(item!.ProfileId)
                || item.ServerSequence <= previous || item.ServerSequence > page.Boundary)
                return output.WithError("persistence_unavailable");
            items.Add(item); previous = item.ServerSequence;
        }
        if (page.HasMore && previous >= page.Boundary) return output.WithError("persistence_unavailable");
        var next = page.HasMore ? cursors.Encode(new(query.Actor, verifier, size, previous, page.Boundary)) : null;
        return output.WithData(new ProfileListOutput(items, next)).WithMessage("profiles_found");
    }
}
