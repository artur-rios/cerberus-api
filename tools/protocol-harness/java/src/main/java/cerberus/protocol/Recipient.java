package cerberus.protocol;

import java.util.*;
import java.util.function.BiFunction;
import org.bouncycastle.crypto.hpke.HPKE;
import org.bouncycastle.crypto.util.PrivateKeyFactory;

/** Native HPKE base confidentiality and a separate pinned-author signature. */
public final class Recipient {
    private Recipient() {}
    static final String FORMAT="cerberus-recipient-wrap-v1";
    static final Set<String> FIELDS=Set.of("format","keyEpoch","grantId","grantRevision","recipientIdentityId","recipientKeyFingerprint","authorKeyFingerprint","enc","ciphertext","signature");
    private static final Set<String> KINDS=Set.of("account","profile","record","folder","collection");
    private static void binding(Context resource,String grantId,long grantRevision,String identity) {
        if(resource==null || !KINDS.contains(resource.resourceKind()) || !resource.extraContext().isEmpty()) throw new ProtocolError();
        ProtocolJson.guid(grantId); ProtocolJson.integer(grantRevision,1,ProtocolJson.MAX_INTEGER); ProtocolJson.guid(identity);
    }
    private static byte[] material(byte[] value,int length) { if(value==null || value.length!=length) throw new ProtocolError(); return value.clone(); }
    private static byte[] info(Context resource,String grantId,long revision,String identity,String recipient,String author) {
        return ProtocolJson.context(List.of(FORMAT,resource.ownerId(),resource.resourceKind(),resource.resourceId(),resource.keyEpoch(),grantId,revision,identity,recipient,author));
    }
    private static byte[] signed(byte[] info,String enc,String ciphertext) {
        return ProtocolJson.context(List.of("cerberus-recipient-signature-v1",ProtocolJson.base64(info),enc,ciphertext));
    }
    private static HPKE suite() { return new HPKE(HPKE.mode_base,HPKE.kem_P256_SHA256,HPKE.kdf_HKDF_SHA256,HPKE.aead_AES_GCM256); }
    private static byte[] point(Map<String,Object> key) {
        Map<String,Object> value=Keys.validatePublic(key); byte[] result=new byte[65]; result[0]=4;
        System.arraycopy(ProtocolJson.unbase64((String)value.get("x"),32),0,result,1,32); System.arraycopy(ProtocolJson.unbase64((String)value.get("y"),32),0,result,33,32); return result;
    }
    private static byte[] decapsulate(byte[] privateDer,byte[] sealed,byte[] info) {
        try {
            HPKE suite=suite(); var key=suite.deserializePrivateKey(suite.serializePrivateKey(PrivateKeyFactory.createKey(privateDer)),point(Keys.publicJwk(privateDer)));
            return suite.setupBaseR(Arrays.copyOf(sealed,65),key,info).open(new byte[0],Arrays.copyOfRange(sealed,65,sealed.length));
        } catch(Exception ex) { throw new ProtocolError(); }
    }
    public static Map<String,Object> wrap(byte[] resourceRoot,Context resource,String grantId,long grantRevision,String recipientIdentityId,Map<String,Object> recipientJwk,byte[] authorPrivateDer) {
        binding(resource,grantId,grantRevision,recipientIdentityId); byte[] root=material(resourceRoot,32);
        Map<String,Object> recipient=ClientTrust.key(recipientJwk); String recipientFingerprint=Keys.thumbprint(recipient),authorFingerprint=Keys.thumbprint(Keys.publicJwk(authorPrivateDer));
        if(recipientFingerprint.equals(authorFingerprint)) throw new ProtocolError();
        byte[] info=info(resource,grantId,grantRevision,recipientIdentityId,recipientFingerprint,authorFingerprint);
        try {
            HPKE suite=suite(); var sender=suite.setupBaseS(suite.deserializePublicKey(point(recipient)),info);
            byte[] enc=sender.getEncapsulation(),ciphertext=sender.seal(new byte[0],root);
            if(enc.length!=65 || ciphertext.length!=48) throw new ProtocolError("unsupported_dependency");
            String encodedEnc=ProtocolJson.base64(enc),encodedCiphertext=ProtocolJson.base64(ciphertext);
            Map<String,Object> result=Map.of("format",FORMAT,"keyEpoch",resource.keyEpoch(),"grantId",grantId,"grantRevision",grantRevision,"recipientIdentityId",recipientIdentityId,
                "recipientKeyFingerprint",recipientFingerprint,"authorKeyFingerprint",authorFingerprint,"enc",encodedEnc,"ciphertext",encodedCiphertext,"signature",ProtocolJson.base64(Keys.sign(authorPrivateDer,signed(info,encodedEnc,encodedCiphertext))));
            Wire.encode(result); return result;
        } catch(ProtocolError ex) { throw ex; }
        catch(Exception ex) { throw new ProtocolError(); }
        finally { Arrays.fill(root,(byte)0); }
    }
    public static byte[] open(Map<String,Object> envelope,Context resource,String grantId,long grantRevision,String recipientIdentityId,byte[] recipientPrivateDer,ClientTrust trust) {
        return openCore(envelope,resource,grantId,grantRevision,recipientIdentityId,recipientPrivateDer,trust,(sealed,info)->decapsulate(recipientPrivateDer,sealed,info));
    }
    static byte[] openCore(Map<String,Object> envelope,Context resource,String grantId,long grantRevision,String recipientIdentityId,byte[] recipientPrivateDer,ClientTrust trust,BiFunction<byte[],byte[],byte[]> decap) {
        binding(resource,grantId,grantRevision,recipientIdentityId); if(trust==null) throw new ProtocolError();
        Map<String,Object> value=Protection.snapshot(envelope,FIELDS); var pins=trust.pins(resource.ownerId());
        String recipientFingerprint=Keys.thumbprint(pins.recipient()),authorFingerprint=Keys.thumbprint(pins.author());
        if(!FORMAT.equals(value.get("format")) || ProtocolJson.integer(value.get("keyEpoch"),1,ProtocolJson.MAX_INTEGER)!=resource.keyEpoch() || !grantId.equals(value.get("grantId"))
            || ProtocolJson.integer(value.get("grantRevision"),1,ProtocolJson.MAX_INTEGER)!=grantRevision || !recipientIdentityId.equals(value.get("recipientIdentityId"))
            || !recipientFingerprint.equals(value.get("recipientKeyFingerprint")) || !authorFingerprint.equals(value.get("authorKeyFingerprint"))) throw new ProtocolError();
        byte[] enc=ProtocolJson.unbase64(ProtocolJson.text(value.get("enc"),true),65),ciphertext=ProtocolJson.unbase64(ProtocolJson.text(value.get("ciphertext"),true),48),signature=ProtocolJson.unbase64(ProtocolJson.text(value.get("signature"),true),64);
        byte[] info=info(resource,grantId,grantRevision,recipientIdentityId,recipientFingerprint,authorFingerprint);
        if(!Keys.verify(pins.author(),signed(info,(String)value.get("enc"),(String)value.get("ciphertext")),signature)) throw new ProtocolError();
        if(!Keys.thumbprint(Keys.publicJwk(recipientPrivateDer)).equals(recipientFingerprint) || enc[0]!=4) throw new ProtocolError();
        Keys.validatePublic(Map.of("crv","P-256","kty","EC","x",ProtocolJson.base64(Arrays.copyOfRange(enc,1,33)),"y",ProtocolJson.base64(Arrays.copyOfRange(enc,33,65))));
        byte[] sealed=Arrays.copyOf(enc,113); System.arraycopy(ciphertext,0,sealed,65,48);
        return material(decap.apply(sealed,info),32);
    }
}
