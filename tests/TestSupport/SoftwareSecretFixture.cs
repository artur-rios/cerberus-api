using System.Security.Cryptography;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Software;

namespace ArturRios.Cerberus.TestSupport;

internal static class SoftwareSecretFixture
{
    internal sealed record Case(SoftwareIdentity Identity,SoftwareSecretInput Input,SoftwareSecretDetails Details);
    internal static Case Create(string kind,bool member)
    {
        using var author=ECDsa.Create(ECCurve.NamedCurves.nistP256);using var recipient=ECDsa.Create(ECCurve.NamedCurves.nistP256);using var ephemeral=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var owner=Guid.NewGuid();var grant=Guid.NewGuid();var identity=new SoftwareIdentity(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()+60));
        var target=new SoftwareSecretResource(kind,Guid.NewGuid(),1,1,Envelope());var provision=member?new SoftwareSecretResource("collection",Guid.NewGuid(),1,1,Envelope()):target;
        var recipientKey=ProtectionFixture.Public(recipient);var authorKey=ProtectionFixture.Public(author);var point=ephemeral.ExportParameters(false).Q;
        var wrap=new RecipientEnvelope("cerberus-recipient-wrap-v1",1,grant,1,identity.ApplicationId,recipientKey.Fingerprint(),authorKey.Fingerprint(),ProtectionFixture.Encode([4,..point.X!,..point.Y!]),ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(48)),"");
        wrap=wrap with{Signature=ProtectionFixture.Encode(author.SignData(wrap.SigningBytes(owner,provision.Kind,provision.Id),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))};
        return new(identity,new(grant,kind,target.Id,member?provision.Id:null),new(owner,grant,1,target,new(provision,wrap),new(recipientKey.Crv,recipientKey.Kty,recipientKey.X,recipientKey.Y),authorKey));
    }
    private static EncryptedEnvelope Envelope()=>new("cerberus-content-v1",1,ProtectionFixture.Encode(new byte[32]),ProtectionFixture.Encode(new byte[12]),"AQID",ProtectionFixture.Encode(new byte[16]));
}
