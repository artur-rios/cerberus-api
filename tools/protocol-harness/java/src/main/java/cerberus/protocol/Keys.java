package cerberus.protocol;

import java.math.BigInteger;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.*;
import org.bouncycastle.asn1.*;
import org.bouncycastle.asn1.pkcs.PrivateKeyInfo;
import org.bouncycastle.asn1.sec.SECNamedCurves;
import org.bouncycastle.asn1.sec.SECObjectIdentifiers;
import org.bouncycastle.asn1.x9.X9ObjectIdentifiers;
import org.bouncycastle.crypto.digests.SHA256Digest;
import org.bouncycastle.crypto.generators.ECKeyPairGenerator;
import org.bouncycastle.crypto.params.*;
import org.bouncycastle.crypto.signers.ECDSASigner;
import org.bouncycastle.crypto.signers.HMacDSAKCalculator;
import org.bouncycastle.crypto.util.PrivateKeyFactory;
import org.bouncycastle.crypto.util.PrivateKeyInfoFactory;
import org.bouncycastle.util.BigIntegers;

/** P256-only keys; signing and all curve/scalar operations belong to BC. */
public final class Keys {
    private Keys() {}
    private static final ECNamedDomainParameters DOMAIN=new ECNamedDomainParameters(SECObjectIdentifiers.secp256r1,SECNamedCurves.getByName("secp256r1"));
    private static ECPrivateKeyParameters privateKey(byte[] der) {
        if(der==null || der.length==0 || der.length>ProtocolJson.MAX_REQUEST) throw new ProtocolError();
        try {
            ASN1Primitive object=ASN1Primitive.fromByteArray(der);
            if(!Arrays.equals(der,object.getEncoded("DER"))) throw new ProtocolError();
            PrivateKeyInfo info=PrivateKeyInfo.getInstance(object);
            if(!info.getVersion().hasValue(0) || !X9ObjectIdentifiers.id_ecPublicKey.equals(info.getPrivateKeyAlgorithm().getAlgorithm())
                || !SECObjectIdentifiers.secp256r1.equals(info.getPrivateKeyAlgorithm().getParameters())) throw new ProtocolError();
            // The PKCS#8 OCTET STRING is opaque to outer DER validation.
            byte[] innerBytes=info.getPrivateKey().getOctets();
            ASN1Primitive inner=ASN1Primitive.fromByteArray(innerBytes);
            if(!Arrays.equals(innerBytes,inner.getEncoded("DER"))) throw new ProtocolError();
            ASN1Sequence sequence=ASN1Sequence.getInstance(inner);
            if(sequence.size()<2 || sequence.size()>4 || !ASN1Integer.getInstance(sequence.getObjectAt(0)).hasValue(1)
                || ASN1OctetString.getInstance(sequence.getObjectAt(1)).getOctets().length!=32) throw new ProtocolError();
            int previous=-1;
            for(int i=2;i<sequence.size();i++) {
                ASN1TaggedObject field=ASN1TaggedObject.getInstance(sequence.getObjectAt(i));
                int tag=field.getTagNo();
                if(!field.hasContextTag() || !field.isExplicit() || tag<0 || tag>1 || tag<=previous) throw new ProtocolError();
                previous=tag;
                if(tag==0 && !SECObjectIdentifiers.secp256r1.equals(field.getExplicitBaseObject())) throw new ProtocolError();
                if(tag==1 && ASN1BitString.getInstance(field.getExplicitBaseObject()).getPadBits()!=0) throw new ProtocolError();
            }
            ECPrivateKeyParameters key=(ECPrivateKeyParameters)PrivateKeyFactory.createKey(info);
            var embedded=org.bouncycastle.asn1.sec.ECPrivateKey.getInstance(inner).getPublicKey();
            if(embedded!=null) {
                var claimed=DOMAIN.getCurve().decodePoint(embedded.getBytes());
                var derived=DOMAIN.getG().multiply(key.getD()).normalize();
                if(!claimed.equals(derived)) throw new ProtocolError();
            }
            return key;
        } catch(Exception ex) { throw new ProtocolError(); }
    }
    public static byte[] generate() {
        try {
            ECKeyPairGenerator generator=new ECKeyPairGenerator(); generator.init(new ECKeyGenerationParameters(DOMAIN,new SecureRandom()));
            return PrivateKeyInfoFactory.createPrivateKeyInfo(generator.generateKeyPair().getPrivate()).getEncoded("DER");
        } catch(Exception ex) { throw new ProtocolError(); }
    }
    public static Map<String,Object> publicJwk(byte[] privateDer) {
        var key=privateKey(privateDer);
        var point=DOMAIN.getG().multiply(key.getD()).normalize();
        return Map.of("crv","P-256","kty","EC","x",ProtocolJson.base64(BigIntegers.asUnsignedByteArray(32,point.getAffineXCoord().toBigInteger())),
            "y",ProtocolJson.base64(BigIntegers.asUnsignedByteArray(32,point.getAffineYCoord().toBigInteger())));
    }
    private static ECPublicKeyParameters publicKey(Map<String,Object> jwk) {
        Map<String,Object> value=ProtocolJson.fields(jwk,Set.of("crv","kty","x","y"),Set.of());
        if(!"P-256".equals(value.get("crv")) || !"EC".equals(value.get("kty")) || !(value.get("x") instanceof String x) || !(value.get("y") instanceof String y)) throw new ProtocolError();
        byte[] encoded=new byte[65]; encoded[0]=4;
        System.arraycopy(ProtocolJson.unbase64(x,32),0,encoded,1,32); System.arraycopy(ProtocolJson.unbase64(y,32),0,encoded,33,32);
        try {
            var point=DOMAIN.getCurve().decodePoint(encoded);
            if(point.isInfinity() || !point.isValid()) throw new ProtocolError();
            return new ECPublicKeyParameters(point,DOMAIN);
        } catch(Exception ex) { throw new ProtocolError(); }
    }
    public static Map<String,Object> validatePublic(Map<String,Object> jwk) { publicKey(jwk); return Map.copyOf(jwk); }
    public static String thumbprint(Map<String,Object> jwk) {
        Map<String,Object> value=validatePublic(jwk);
        // Every value was validated as fixed ASCII or canonical base64url; none needs JSON escaping.
        String canonical="{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\""+value.get("x")+"\",\"y\":\""+value.get("y")+"\"}";
        return ProtocolJson.base64(Primitives.sha256(canonical.getBytes(StandardCharsets.US_ASCII)));
    }
    public static byte[] sign(byte[] privateDer,byte[] message) {
        if(message==null) throw new ProtocolError();
        var privateKey=privateKey(privateDer);
        try {
            ECDSASigner signer=new ECDSASigner(new HMacDSAKCalculator(new SHA256Digest())); signer.init(true,privateKey);
            BigInteger[] pair=signer.generateSignature(Primitives.sha256(message));
            byte[] result=new byte[64]; System.arraycopy(BigIntegers.asUnsignedByteArray(32,pair[0]),0,result,0,32);
            System.arraycopy(BigIntegers.asUnsignedByteArray(32,pair[1]),0,result,32,32); return result;
        } catch(Exception ex) { throw new ProtocolError(); }
    }
    public static boolean verify(Map<String,Object> jwk,byte[] message,byte[] signature) {
        var key=publicKey(jwk);
        if(message==null) throw new ProtocolError();
        if(signature==null || signature.length!=64) return false;
        BigInteger r=new BigInteger(1,Arrays.copyOfRange(signature,0,32)),s=new BigInteger(1,Arrays.copyOfRange(signature,32,64));
        if(r.signum()<=0 || r.compareTo(DOMAIN.getN())>=0 || s.signum()<=0 || s.compareTo(DOMAIN.getN())>=0) return false;
        try { ECDSASigner verifier=new ECDSASigner(); verifier.init(false,key); return verifier.verifySignature(Primitives.sha256(message),r,s); }
        catch(Exception ex) { throw new ProtocolError(); }
    }
}
