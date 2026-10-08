package cerberus.protocol;

import java.nio.charset.StandardCharsets;
import java.util.*;
import org.bouncycastle.crypto.generators.Argon2BytesGenerator;
import org.bouncycastle.crypto.params.Argon2Parameters;
import org.bouncycastle.crypto.hpke.*;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;
import static cerberus.protocol.Fixtures.*;

class PrimitivesTest {
    private byte[] primitive(String method, byte[] a,byte[] b,byte[] c,byte[] d) {
        return (byte[])call("Primitives",method,new Class<?>[]{byte[].class,byte[].class,byte[].class,byte[].class},a,b,c,d);
    }
    private byte[] hkdf(byte[] root,byte[] salt,byte[] info,int length) {
        return (byte[])call("Primitives","hkdf",new Class<?>[]{byte[].class,byte[].class,byte[].class,int.class},root,salt,info,length);
    }
    private byte[] argon2(byte[] password,byte[] salt) {
        return (byte[])call("Primitives","argon2",new Class<?>[]{byte[].class,byte[].class},password,salt);
    }
    private byte[] sha256(byte[] value) { return (byte[])call("Primitives","sha256",new Class<?>[]{byte[].class},(Object)value); }
    @Test void GivenRfc5869Case1_WhenDerived_ThenKnownBytesMatch() {
        Map<String,Object> v=known("hkdf");
        assertArrayEquals(hex(v,"okm"),hkdf(hex(v,"ikm"),hex(v,"salt"),hex(v,"info"),((Number)v.get("length")).intValue()));
    }
    @Test void GivenInvalidHkdfInputs_WhenDerived_ThenRedactedRejection() {
        rejected(()->hkdf(null,new byte[0],new byte[0],32));
        rejected(()->hkdf(new byte[0],new byte[0],new byte[0],32));
        rejected(()->hkdf(new byte[1],null,new byte[0],32));
        rejected(()->hkdf(new byte[1],new byte[0],null,32));
        rejected(()->hkdf(new byte[1],new byte[0],new byte[0],0));
        rejected(()->hkdf(new byte[1],new byte[0],new byte[0],8161));
    }
    @Test void GivenNistAes256GcmCases_WhenSealed_ThenPublishedCiphertextAndTagMatch() {
        for(Object value:(List<?>)known("gcm").get("cases")) {
            Map<String,Object> v=map(value);
            assertArrayEquals(HexFormat.of().parseHex((String)v.get("CT")+(String)v.get("Tag")),primitive("gcmSeal",hex(v,"Key"),hex(v,"IV"),hex(v,"AAD"),hex(v,"PT")));
        }
    }
    @Test void GivenNistAes256GcmCases_WhenOpened_ThenPublishedPlaintextMatches() {
        for(Object value:(List<?>)known("gcm").get("cases")) {
            Map<String,Object> v=map(value);
            assertArrayEquals(hex(v,"PT"),primitive("gcmOpen",hex(v,"Key"),hex(v,"IV"),hex(v,"AAD"),HexFormat.of().parseHex((String)v.get("CT")+(String)v.get("Tag"))));
        }
    }
    @Test void GivenChangedTagOrAad_WhenOpened_ThenNoPlaintextIsReturned() {
        Map<String,Object> v=map(((List<?>)known("gcm").get("cases")).get(2));
        byte[] ct=HexFormat.of().parseHex((String)v.get("CT")+(String)v.get("Tag"));
        byte[] changed=ct.clone(); changed[changed.length-1]^=1;
        rejected(()->primitive("gcmOpen",hex(v,"Key"),hex(v,"IV"),hex(v,"AAD"),changed));
        rejected(()->primitive("gcmOpen",hex(v,"Key"),hex(v,"IV"),new byte[]{1},ct));
    }
    @Test void GivenInvalidGcmInputs_WhenUsed_ThenRedactedRejection() {
        for(String method:List.of("gcmSeal","gcmOpen")) {
            rejected(()->primitive(method,new byte[16],new byte[12],new byte[0],new byte[16]));
            rejected(()->primitive(method,new byte[32],new byte[11],new byte[0],new byte[16]));
            rejected(()->primitive(method,null,new byte[12],new byte[0],new byte[16]));
            rejected(()->primitive(method,new byte[32],new byte[12],null,new byte[16]));
            rejected(()->primitive(method,new byte[32],new byte[12],new byte[0],null));
        }
        rejected(()->primitive("gcmOpen",new byte[32],new byte[12],new byte[0],new byte[15]));
    }
    @Test void GivenRfc9106Argon2id_WhenNativeDerived_ThenPublishedTagMatches() {
        Map<String,Object> v=known("argon2");
        Argon2BytesGenerator generator=new Argon2BytesGenerator();
        generator.init(new Argon2Parameters.Builder(Argon2Parameters.ARGON2_id).withVersion(Argon2Parameters.ARGON2_VERSION_13)
            .withMemoryAsKB(32).withIterations(3).withParallelism(4).withSalt(hex(v,"salt")).withSecret(hex(v,"secret")).withAdditional(hex(v,"ad")).build());
        byte[] result=new byte[32]; generator.generateBytes(hex(v,"password"),result);
        assertArrayEquals(hex(v,"tag"),result);
    }
    @Test void GivenProtocolPassword_WhenDerived_ThenFixedProfileIsUsed() {
        byte[] salt=new byte[16]; Arrays.fill(salt,(byte)'s');
        byte[] password="password".getBytes(StandardCharsets.UTF_8);
        Argon2BytesGenerator generator=new Argon2BytesGenerator();
        generator.init(new Argon2Parameters.Builder(Argon2Parameters.ARGON2_id).withVersion(Argon2Parameters.ARGON2_VERSION_13)
            .withMemoryAsKB(65536).withIterations(3).withParallelism(4).withSalt(salt).build());
        byte[] expected=new byte[32]; generator.generateBytes(password,expected);
        assertArrayEquals(expected,argon2(password,salt));
    }
    @Test void GivenInvalidArgon2Inputs_WhenDerived_ThenRedactedRejection() {
        rejected(()->argon2(null,new byte[16]));
        rejected(()->argon2(new byte[1],null));
        rejected(()->argon2(new byte[1],new byte[15]));
    }
    @Test void GivenRfc7638CanonicalRsaBytes_WhenHashed_ThenPublishedThumbprintMatches() {
        Map<String,Object> v=known("thumbprint");
        assertEquals(v.get("value"),ProtocolJson.base64(sha256(((String)v.get("canonical")).getBytes(StandardCharsets.US_ASCII))));
    }
    @Test void GivenSha256EmptyAndAbc_WhenHashed_ThenKnownDigestsMatch() {
        assertEquals("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",HexFormat.of().formatHex(sha256(new byte[0])));
        assertEquals("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",HexFormat.of().formatHex(sha256(new byte[]{'a','b','c'})));
        rejected(()->sha256(null));
    }
    @Test void GivenRfc9180BaseSuite_WhenNativeOpenedAndSealed_ThenPublishedBytesMatch() throws Exception {
        Map<String,Object> v=known("hpke");
        HPKE suite=new HPKE(HPKE.mode_base,HPKE.kem_P256_SHA256,HPKE.kdf_HKDF_SHA256,HPKE.aead_AES_GCM256);
        var recipient=suite.deserializePrivateKey(hex(v,"skRm"),hex(v,"pkRm"));
        var sender=suite.setupBaseS(recipient.getPublic(),hex(v,"info"),suite.deserializePrivateKey(hex(v,"skEm"),hex(v,"pkEm")));
        assertArrayEquals(hex(v,"enc"),sender.getEncapsulation());
        var receiver=suite.setupBaseR(hex(v,"enc"),recipient,hex(v,"info"));
        for(Object value:(List<?>)v.get("encryptions")) {
            Map<String,Object> c=map(value);
            assertArrayEquals(hex(c,"ct"),sender.seal(hex(c,"aad"),hex(c,"pt")));
            assertArrayEquals(hex(c,"pt"),receiver.open(hex(c,"aad"),hex(c,"ct")));
        }
        var changedReceiver=suite.setupBaseR(hex(v,"enc"),recipient,hex(v,"info"));
        Map<String,Object> c=map(((List<?>)v.get("encryptions")).getFirst());
        byte[] changed=hex(c,"ct"); changed[changed.length-1]^=1;
        assertThrows(org.bouncycastle.crypto.InvalidCipherTextException.class,()->changedReceiver.open(hex(c,"aad"),changed));
    }
}
