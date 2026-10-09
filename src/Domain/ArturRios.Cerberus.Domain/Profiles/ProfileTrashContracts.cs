using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Profiles;

public sealed record ProfileTrashRequest(Guid Actor, string AccessVerifier, Guid ProfileId, long ExpectedRevision);
public sealed record ProfileTrashDetails(Guid ProfileId, Guid TrashOperationId, long Revision, long ServerSequence,
    DateTimeOffset DeletedAt, DateTimeOffset PurgeAt);
public interface IProfileTrashStore
{
    Task<VaultResult<ProfileTrashDetails>> TrashAsync(ProfileTrashRequest request, CancellationToken cancellationToken);
}
