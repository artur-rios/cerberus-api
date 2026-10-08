using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Profiles;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ProfileAssociationInput(long ExpectedRevision,Guid[] RecordIds,Guid[] FolderIds,Guid[] CollectionIds)
{
    public bool IsValid()=>ExpectedRevision is >0 and <=ProtocolBinary.MaxInteger && ValidIds(RecordIds) && ValidIds(FolderIds) && ValidIds(CollectionIds);
    public static bool ValidIds(Guid[]? ids)=>ids is not null && ids.All(x=>x!=Guid.Empty) && ids.Distinct().Count()==ids.Length;
}
public sealed record ProfileAssociationRequest(Guid Actor,string AccessVerifier,Guid ProfileId,ProfileAssociationInput Input);
public sealed record ProfileAssociationDetails(Guid ProfileId,long Revision,long ServerSequence,Guid[] RecordIds,Guid[] FolderIds,Guid[] CollectionIds);
public interface IProfileAssociationStore
{
    Task<VaultResult<ProfileAssociationDetails>> SetAsync(ProfileAssociationRequest request,CancellationToken cancellationToken);
}
