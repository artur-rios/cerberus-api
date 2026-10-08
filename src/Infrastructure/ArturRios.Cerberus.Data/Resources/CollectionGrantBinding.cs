using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
namespace ArturRios.Cerberus.Data.Resources;

internal static class CollectionGrantBinding
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){PropertyNameCaseInsensitive=false,AllowDuplicateProperties=false};
    internal static bool TryReadPins(VaultProtection? row,out ProtectionMaterial? pins)
    {
        pins=null;
        try
        {
            if(row is null || row.Revision is <=0 or >ProtocolBinary.MaxInteger)return false;
            pins=JsonSerializer.Deserialize<ProtectionMaterial>(row.Material,Json);
            return pins?.IsValid()==true && row.KeyEpoch==pins.PasswordWrapper.KeyEpoch && row.RecoveryGeneration==pins.RecoveryWrapper.Generation;
        }
        catch(JsonException){return false;}
    }
    internal static bool IsBound(CollectionGrant grant,VaultCollection collection,Guid owner,Guid identity,ProtectionMaterial ownerPins,ProtectionMaterial recipientPins)
    {
        try
        {
            var envelope=JsonSerializer.Deserialize<EncryptedEnvelope>(collection.Envelope,Json);
            var key=JsonSerializer.Deserialize<RecipientEnvelope>(grant.RecipientKeyEnvelope,Json);
            return envelope?.IsValid()==true && envelope.KeyEpoch==collection.KeyEpoch
                && grant.Revision is >0 and <=ProtocolBinary.MaxInteger
                && key?.Verify(owner,"collection",collection.PublicId,collection.KeyEpoch,grant.PublicId,grant.Revision,
                    identity,recipientPins.RecipientKey,ownerPins.AuthorKey)==true;
        }
        catch(JsonException){return false;}
    }
}
