using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArturRios.Cerberus.Domain.Protection;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record RecipientEnvelope(string Format, long KeyEpoch, Guid GrantId, long GrantRevision,
    Guid RecipientIdentityId, string RecipientKeyFingerprint, string AuthorKeyFingerprint, string Enc, string Ciphertext, string Signature)
{
    public bool IsValid()
    {
        if (Format != "cerberus-recipient-wrap-v1" || KeyEpoch is <= 0 or > ProtocolBinary.MaxInteger
            || GrantRevision is <= 0 or > ProtocolBinary.MaxInteger || GrantId == Guid.Empty || RecipientIdentityId == Guid.Empty
            || !ProtocolBinary.TryDecode(RecipientKeyFingerprint,32,out _) || !ProtocolBinary.TryDecode(AuthorKeyFingerprint,32,out _)
            || !ProtocolBinary.TryDecode(Enc,65,out var point) || point[0] != 4
            || !ProtocolBinary.TryDecode(Ciphertext,48,out _) || !ProtocolBinary.TryDecode(Signature,64,out _)) return false;
        return new PublicJwk("P-256","EC",ProtocolBinary.Encode(point[1..33]),ProtocolBinary.Encode(point[33..65])).IsValid();
    }
    public byte[] SigningBytes(Guid owner, string kind, Guid resource)
    {
        var info=JsonSerializer.SerializeToUtf8Bytes(new object[] {"cerberus-recipient-wrap-v1",owner.ToString("D"),kind,resource.ToString("D"),
            KeyEpoch,GrantId.ToString("D"),GrantRevision,RecipientIdentityId.ToString("D"),RecipientKeyFingerprint,AuthorKeyFingerprint});
        return JsonSerializer.SerializeToUtf8Bytes(new[] {"cerberus-recipient-signature-v1",ProtocolBinary.Encode(info),Enc,Ciphertext});
    }
    public bool Verify(Guid owner, string kind, Guid resource, long epoch, Guid grant, long revision, Guid identity, PublicJwk recipient, PublicJwk author)
    {
        if (!IsValid() || owner == Guid.Empty || resource == Guid.Empty || kind is not ("account" or "profile" or "record" or "folder" or "collection")
            || KeyEpoch != epoch || GrantId != grant || GrantRevision != revision || RecipientIdentityId != identity
            || recipient?.IsValid() != true || author?.IsValid() != true || RecipientKeyFingerprint != recipient.Fingerprint()
            || AuthorKeyFingerprint != author.Fingerprint() || RecipientKeyFingerprint == AuthorKeyFingerprint) return false;
        ProtocolBinary.TryDecode(Signature,64,out var signature);
        return author.Verify(SigningBytes(owner,kind,resource),signature);
    }
}
