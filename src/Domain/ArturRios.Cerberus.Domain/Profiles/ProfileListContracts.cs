using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Profiles;

public sealed record ProfileListRequest(Guid Actor, string AccessVerifier, int PageSize, long After, long? Boundary);
public sealed record ProfileListRow(Guid ProfileId, long Revision, long ServerSequence, DateTimeOffset EditedAt, byte[] Envelope, byte[] KeyWrappers);
public sealed record ProfileListPage(IReadOnlyList<ProfileListRow> Items, long Boundary, bool HasMore);
public interface IProfileListStore
{
    Task<VaultResult<ProfileListPage>> ListAsync(ProfileListRequest request, CancellationToken cancellationToken);
}
