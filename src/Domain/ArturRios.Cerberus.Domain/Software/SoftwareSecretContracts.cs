using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Software;

public sealed record SoftwareSecretInput(Guid GrantId,string Kind,Guid Id,Guid? CollectionId=null)
{
    public bool IsValid()=>GrantId!=Guid.Empty&&Id!=Guid.Empty&&Kind is "record" or "collection"
        &&(CollectionId is null||Kind=="record"&&CollectionId!=Guid.Empty);
}
public sealed record SoftwareSecretRequest(SoftwareIdentity Identity,SoftwareSecretInput Input);

public sealed record SoftwareSecretResource(
    [property:JsonRequired]string Kind,[property:JsonRequired]Guid Id,[property:JsonRequired]long Revision,
    [property:JsonRequired]long KeyEpoch,[property:JsonRequired]EncryptedEnvelope Envelope)
{
    public bool IsValid()=>Kind is "record" or "collection"&&Id!=Guid.Empty&&Revision is >0 and <=ProtocolBinary.MaxInteger
        &&KeyEpoch is >0 and <=ProtocolBinary.MaxInteger&&Envelope?.IsValid()==true&&Envelope.KeyEpoch==KeyEpoch;
}
public sealed record SoftwareSecretProvisioning([property:JsonRequired]SoftwareSecretResource Resource,[property:JsonRequired]RecipientEnvelope KeyEnvelope);
public sealed record SoftwareSecretDetails(
    [property:JsonRequired]Guid OwnerAccountId,[property:JsonRequired]Guid GrantId,[property:JsonRequired]long GrantRevision,
    [property:JsonRequired]SoftwareSecretResource Target,[property:JsonRequired]SoftwareSecretProvisioning Provisioning,
    [property:JsonRequired]SoftwareRecipientKey RecipientKey,[property:JsonRequired]PublicJwk AuthorKey)
{
    public bool IsValid(SoftwareIdentity identity,SoftwareSecretInput input)
    {
        if(identity?.IsValid()!=true||input?.IsValid()!=true||OwnerAccountId==Guid.Empty||GrantId!=input.GrantId
            ||GrantRevision is <=0 or >ProtocolBinary.MaxInteger||Target?.IsValid()!=true||Target.Kind!=input.Kind||Target.Id!=input.Id
            ||Provisioning?.Resource?.IsValid()!=true||RecipientKey?.IsValid()!=true||AuthorKey?.IsValid()!=true)return false;
        var resource=Provisioning.Resource;
        if(input.CollectionId is { } collection)
        {if(resource.Kind!="collection"||resource.Id!=collection)return false;}
        else if(resource!=Target)return false;
        return Provisioning.KeyEnvelope?.Verify(OwnerAccountId,resource.Kind,resource.Id,resource.KeyEpoch,GrantId,GrantRevision,
            identity.ApplicationId,RecipientKey.ToPublicJwk(),AuthorKey)==true;
    }
}
public interface ISoftwareSecretStore
{
    Task<VaultResult<SoftwareSecretDetails>> ReadAsync(SoftwareSecretRequest request,CancellationToken cancellationToken);
}
