using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Profiles;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ProfileAccessInput([property:JsonRequired] long ExpectedRevision)
{
    public bool IsValid()=>ExpectedRevision is >0 and <=ProtocolBinary.MaxInteger;
}
public sealed record ProfileChallengeRequest(Guid Actor,Guid ProfileId,long ExpectedRevision,string RequestHash)
{
    public bool IsValid()=>Actor!=Guid.Empty && ProfileId!=Guid.Empty && new ProfileAccessInput(ExpectedRevision).IsValid()
        && ProtocolBinary.TryDecode(RequestHash,32,out _);
}
public sealed record ProfileAccessRequest(Guid Actor,Guid ProfileId,long ExpectedRevision,Guid ChallengeId,string Proof,byte[] RawBody)
{
    public bool IsValid()=>Actor!=Guid.Empty && ProfileId!=Guid.Empty && new ProfileAccessInput(ExpectedRevision).IsValid()
        && ChallengeId!=Guid.Empty && ProtocolBinary.TryDecode(Proof,64,out _) && RawBody is {Length:>0 and <=1048576};
}
public sealed record ProfileAccessMaterial(Guid AccountId,Guid ProfileId,long Revision,EncryptedEnvelope Envelope,ProfileKeyWrappers KeyWrappers)
{
    public bool IsValid(Guid actor,Guid profileId,long revision)=>actor!=Guid.Empty && AccountId!=Guid.Empty && ProfileId!=Guid.Empty
        && ProfileId==profileId && Revision==revision && Revision is >0 and <=ProtocolBinary.MaxInteger
        && Envelope?.IsValid()==true && KeyWrappers?.IsValid()==true
        && KeyWrappers.MasterKeyWrapper.GrantId==ProfileId && KeyWrappers.MasterKeyWrapper.RecipientIdentityId==actor
        && KeyWrappers.MasterKeyWrapper.GrantRevision<=Revision && KeyWrappers.MasterKeyWrapper.KeyEpoch==Envelope.KeyEpoch;
}
public sealed record ProfileAccessContext(Guid ProfileId,long Revision,long ServerSequence,DateTimeOffset EditedAt,
    EncryptedEnvelope Envelope,ProfileKeyWrappers KeyWrappers,Guid[] RecordIds,Guid[] FolderIds,Guid[] CollectionIds)
{
    public bool IsValid(Guid accountId,Guid actor,Guid profileId,long revision)=>
        new ProfileAccessMaterial(accountId,ProfileId,Revision,Envelope,KeyWrappers).IsValid(actor,profileId,revision)
        && ServerSequence is >0 and <=ProtocolBinary.MaxInteger && EditedAt.Ticks>=TimeSpan.TicksPerMicrosecond
        && EditedAt.Offset==TimeSpan.Zero && EditedAt.Ticks%TimeSpan.TicksPerMicrosecond==0
        && ProfileAssociationInput.ValidIds(RecordIds) && ProfileAssociationInput.ValidIds(FolderIds) && ProfileAssociationInput.ValidIds(CollectionIds);
}
public sealed record ProfileChallengeDetails(VaultProofChallenge Challenge,ProfileAccessMaterial Profile);
public sealed record ProfileAccessDetails(Guid AccountId,string Access,DateTimeOffset IssuedAt,DateTimeOffset? ExpiresAt,ProfileAccessContext Profile);
public interface IProfileAccessStore
{
    Task<VaultResult<ProfileChallengeDetails>> ChallengeAsync(ProfileChallengeRequest request,CancellationToken cancellationToken);
    Task<VaultResult<ProfileAccessDetails>> OpenAsync(ProfileAccessRequest request,CancellationToken cancellationToken);
}
