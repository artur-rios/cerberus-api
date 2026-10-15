using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Trash;

public sealed record TrashEmptyRequest(Guid Actor,string AccessVerifier,long ExpectedRevision);
public sealed record TrashPurgedCount(string Kind,long Count);
public sealed record TrashEmptyDetails(Guid AccountId,DateTimeOffset PurgedAt,IReadOnlyList<TrashPurgedCount> Counts,long TotalPurged);
public interface ITrashEmptyStore
{
    Task<VaultResult<TrashEmptyDetails>> EmptyAsync(TrashEmptyRequest request,CancellationToken cancellationToken);
}
