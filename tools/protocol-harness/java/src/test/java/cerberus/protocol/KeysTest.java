package cerberus.protocol;

import java.math.BigInteger;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.*;
import org.bouncycastle.asn1.sec.SECObjectIdentifiers;
import org.bouncycastle.asn1.sec.SECNamedCurves;
import org.bouncycastle.asn1.pkcs.PrivateKeyInfo;
import org.bouncycastle.crypto.generators.ECKeyPairGenerator;
import org.bouncycastle.crypto.params.*;
import org.bouncycastle.crypto.util.PrivateKeyInfoFactory;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;
import static cerberus.protocol.Fixtures.*;

class KeysTest {
    private static final BigInteger ORDER=new BigInteger("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551",16);
    private byte[] sign(byte[] key,byte[] message) { return (byte[])call("Keys","sign",new Class<?>[]{byte[].class,byte[].class},key,message); }
    private boolean verify(Map<String,Object> key,byte[] message,byte[] signature) {
        return (boolean)call("Keys","verify",new Class<?>[]{Map.class,byte[].class,byte[].class},key,message,signature);
    }
    private Map<String,Object> publicKey(byte[] key) { return map(call("Keys","publicJwk",new Class<?>[]{byte[].class},(Object)key)); }
    private String thumbprint(Map<String,Object> key) { return (String)call("Keys","thumbprint",new Class<?>[]{Map.class},key); }
    private void validate(Map<String,Object> key) { call("Keys","validatePublic",new Class<?>[]{Map.class},key); }
    @Test void GivenRfc6979PrivateKey_WhenSigned_ThenPublishedP1363SignatureMatches() {
        assertArrayEquals(hex(known("ecdsa"),"signature"),sign(privateDer("author"),"sample".getBytes(StandardCharsets.US_ASCII)));
    }
    @Test void GivenRepeatedMessage_WhenSigned_ThenSameDeterministicSignature() {
        assertArrayEquals(sign(privateDer("author"),new byte[]{1}),sign(privateDer("author"),new byte[]{1}));
    }
    @Test void GivenPublishedSignature_WhenVerified_ThenAccepted() {
        assertTrue(verify(publicJwk("author"),"sample".getBytes(StandardCharsets.US_ASCII),hex(known("ecdsa"),"signature")));
    }
    @Test void GivenWrongMessageKeyOrSignature_WhenVerified_ThenRejected() {
        byte[] signature=hex(known("ecdsa"),"signature");
        assertFalse(verify(publicJwk("author"),"wrong".getBytes(StandardCharsets.US_ASCII),signature));
        assertFalse(verify(publicJwk("lease"),"sample".getBytes(StandardCharsets.US_ASCII),signature));
        signature[63]^=1;
        assertFalse(verify(publicJwk("author"),"sample".getBytes(StandardCharsets.US_ASCII),signature));
    }
    @Test void GivenInvalidSignatureScalarsOrLengths_WhenVerified_ThenRejected() {
        byte[] scalar=org.bouncycastle.util.BigIntegers.asUnsignedByteArray(32,ORDER);
        byte[] r=new byte[64]; System.arraycopy(scalar,0,r,0,32); r[63]=1;
        byte[] s=new byte[64]; s[31]=1; System.arraycopy(scalar,0,s,32,32);
        for(byte[] value:Arrays.asList(null,new byte[0],new byte[63],new byte[65],new byte[64],r,s))
            assertFalse(verify(publicJwk("author"),new byte[]{1},value));
    }
    @Test void GivenHighAndLowSSignatures_WhenVerified_ThenBothAccepted() {
        byte[] signature=hex(known("ecdsa"),"signature");
        byte[] s=org.bouncycastle.util.BigIntegers.asUnsignedByteArray(32,ORDER.subtract(new BigInteger(1,Arrays.copyOfRange(signature,32,64))));
        System.arraycopy(s,0,signature,32,32);
        assertTrue(verify(publicJwk("author"),"sample".getBytes(StandardCharsets.US_ASCII),signature));
    }
    @Test void GivenRfc6979PrivateDer_WhenPublicExported_ThenPublishedCoordinatesMatch() {
        Map<String,Object> jwk=publicKey(privateDer("author"));
        assertArrayEquals(hex(known("ecdsa"),"x"),ProtocolJson.unbase64((String)jwk.get("x"),32));
        assertArrayEquals(hex(known("ecdsa"),"y"),ProtocolJson.unbase64((String)jwk.get("y"),32));
    }
    @Test void GivenP256Jwk_WhenThumbprinted_ThenIndependentCanonicalHashMatches() {
        assertEquals("DOvxvJiAdIqVWIkFt5hDtCunXLF0BV4-JGv4f-ALSm0",thumbprint(publicJwk("author")));
        Map<String,Object> reordered=new LinkedHashMap<>();
        for(String k:List.of("y","x","kty","crv")) reordered.put(k,publicJwk("author").get(k));
        assertEquals("DOvxvJiAdIqVWIkFt5hDtCunXLF0BV4-JGv4f-ALSm0",thumbprint(reordered));
    }
    @Test void GivenInvalidKeyMaterial_WhenImported_ThenRedactedRejection() {
        for(Map<String,?> changes:List.of(Map.of("x",ProtocolJson.base64(new byte[32]),"y",ProtocolJson.base64(new byte[32])),
            Map.of("x","","y",""),Map.of("crv","P-384"),Map.of("kty","RSA"),Map.of("x",publicJwk("author").get("x")+"="),Map.of("d","secret-test-only"))) {
            Map<String,Object> key=new LinkedHashMap<>(publicJwk("author")); key.putAll(changes); rejected(()->validate(key));
        }
        rejected(()->validate(null)); rejected(()->validate(Map.of()));
        Map<String,Object> missing=new LinkedHashMap<>(publicJwk("author")); missing.remove("y"); rejected(()->validate(missing));
    }
    @Test void GivenMalformedTrailingWrongCurveOrNonPkcs8Der_WhenImported_ThenRedactedRejection() throws Exception {
        var curve=SECNamedCurves.getByName("secp384r1");
        var generator=new ECKeyPairGenerator();
        generator.init(new ECKeyGenerationParameters(new ECNamedDomainParameters(SECObjectIdentifiers.secp384r1,curve),new SecureRandom()));
        byte[] other=PrivateKeyInfoFactory.createPrivateKeyInfo(generator.generateKeyPair().getPrivate()).getEncoded();
        byte[] valid=privateDer("author"), trailing=Arrays.copyOf(valid,valid.length+1);
        byte[] sec1=PrivateKeyInfo.getInstance(valid).parsePrivateKey().toASN1Primitive().getEncoded();
        for(byte[] key:Arrays.asList(null,new byte[0],"malformed-secret".getBytes(StandardCharsets.US_ASCII),trailing,Arrays.copyOf(valid,valid.length-1),other,sec1)) {
            rejected(()->publicKey(key)); rejected(()->sign(key,new byte[]{1}));
        }
    }
    @Test void GivenGeneratedPrivateKeys_WhenUsed_ThenPkcs8RoundTrips() {
        byte[] privateKey=(byte[])call("Keys","generate",new Class<?>[]{});
        Map<String,Object> jwk=publicKey(privateKey);
        assertTrue(verify(jwk,new byte[]{1},sign(privateKey,new byte[]{1})));
        assertNotEquals(jwk,publicKey((byte[])call("Keys","generate",new Class<?>[]{})));
    }
    @Test void GivenMismatchedEmbeddedPublicKey_WhenPrivateImported_ThenRedactedRejection() {
        byte[] der=privateDer("author");
        byte[] point=new byte[65]; point[0]=4;
        System.arraycopy(hex(known("ecdsa"),"x"),0,point,1,32); System.arraycopy(hex(known("ecdsa"),"y"),0,point,33,32);
        assertArrayEquals(point,Arrays.copyOfRange(der,der.length-65,der.length));
        System.arraycopy(ProtocolJson.unbase64((String)publicJwk("lease").get("x"),32),0,der,der.length-64,32);
        System.arraycopy(ProtocolJson.unbase64((String)publicJwk("lease").get("y"),32),0,der,der.length-32,32);
        rejected(()->publicKey(der));
    }
    @Test void GivenInvalidPrivateScalar_WhenImported_ThenRedactedRejection() throws Exception {
        var algorithm=PrivateKeyInfo.getInstance(privateDer("author")).getPrivateKeyAlgorithm();
        for(BigInteger scalar:List.of(BigInteger.ZERO,ORDER)) {
            byte[] der=new PrivateKeyInfo(algorithm,new org.bouncycastle.asn1.sec.ECPrivateKey(256,scalar)).getEncoded("DER");
            rejected(()->publicKey(der));
        }
    }
    @Test void GivenPublicFixtureRoles_WhenInspected_ThenKeyPairsAreDistinct() {
        Set<String> fingerprints=new HashSet<>();
        for(String role:List.of("author","recipient","unlock-account","unlock-profile","recovery-1","recovery-2","lease")) fingerprints.add(thumbprint(publicJwk(role)));
        assertEquals(7,fingerprints.size());
    }
    @Test void GivenNullMessage_WhenSigned_ThenRedactedRejection() { rejected(()->sign(privateDer("author"),null)); }
}
