using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Profiles;
public sealed record ProfileReadRequest(Guid Actor, string AccessVerifier, Guid ProfileId);
public sealed record ProfileReadDetails(ProfileListRow Profile, Guid[] RecordIds, Guid[] FolderIds, Guid[] CollectionIds);
public interface IProfileReadStore
{
    Task<VaultResult<ProfileReadDetails>> ReadAsync(ProfileReadRequest request, CancellationToken cancellationToken);
}
