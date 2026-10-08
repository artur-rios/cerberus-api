using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
namespace ArturRios.Cerberus.Query.Profiles;
public sealed record ProfileListItem(Guid ProfileId, long Revision, long ServerSequence, DateTimeOffset EditedAt, EncryptedEnvelope Envelope, ProfileKeyWrappers KeyWrappers);
public sealed class ProfileListOutput(IReadOnlyList<ProfileListItem> items, string? nextCursor) : ArturRios.Mediator.Query.QueryOutput
{
    public IReadOnlyList<ProfileListItem> Items { get; } = items;
    public string? NextCursor { get; } = nextCursor;
}
