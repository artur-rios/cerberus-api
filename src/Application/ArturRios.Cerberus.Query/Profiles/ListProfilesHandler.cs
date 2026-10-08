using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
namespace ArturRios.Cerberus.Query.Profiles;

public sealed class ListProfilesHandler(IProfileListStore store, CerberusOptions options, ProfileListCursor cursors)
    : IQueryHandlerAsync<ListProfilesQuery, ProfileListOutput>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict };
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
            if (row is null || row.ProfileId == Guid.Empty || !ids.Add(row.ProfileId) || row.Revision is <= 0 or > ProtocolBinary.MaxInteger
                || row.ServerSequence <= previous || row.ServerSequence > page.Boundary || row.EditedAt == default
                || row.EditedAt.Offset != TimeSpan.Zero || row.EditedAt.Ticks % TimeSpan.TicksPerMicrosecond != 0
                || row.Envelope is null || row.KeyWrappers is null) return output.WithError("persistence_unavailable");
            EncryptedEnvelope? envelope; ProfileKeyWrappers? wrappers;
            try
            {
                envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope, Json);
                wrappers = JsonSerializer.Deserialize<ProfileKeyWrappers>(row.KeyWrappers, Json);
            }
            catch (JsonException) { return output.WithError("persistence_unavailable"); }
            if (envelope?.IsValid() != true || wrappers?.IsValid() != true || wrappers.MasterKeyWrapper.GrantId != row.ProfileId
                || wrappers.MasterKeyWrapper.RecipientIdentityId != query.Actor
                || wrappers.MasterKeyWrapper.GrantRevision > row.Revision || wrappers.MasterKeyWrapper.KeyEpoch != envelope.KeyEpoch)
                return output.WithError("persistence_unavailable");
            items.Add(new(row.ProfileId, row.Revision, row.ServerSequence, row.EditedAt, envelope, wrappers)); previous = row.ServerSequence;
        }
        if (page.HasMore && previous >= page.Boundary) return output.WithError("persistence_unavailable");
        var next = page.HasMore ? cursors.Encode(new(query.Actor, verifier, size, previous, page.Boundary)) : null;
        return output.WithData(new ProfileListOutput(items, next)).WithMessage("profiles_found");
    }
}
