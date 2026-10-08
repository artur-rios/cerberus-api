package cerberus.protocol;

import java.security.SecureRandom;
import java.util.*;

public final class Symmetric {
    private Symmetric() {}
    private static final String CONTENT="cerberus-content-v1";
    private static final Set<String> KINDS=Set.of("account","profile","record","folder","collection");
    private static final Set<String> FIELDS=Set.of("format","keyEpoch","keySalt","nonce","ciphertext","tag");
    private static final SecureRandom RANDOM=new SecureRandom();
    private static byte[] material(byte[] input,int length) {
        if(input==null || input.length!=length) throw new ProtocolError();
        return input.clone();
    }
    private static void expected(Context expected,String format) {
        if(expected==null || !CONTENT.equals(format) || !KINDS.contains(expected.resourceKind()) || !expected.extraContext().isEmpty()) throw new ProtocolError();
    }
    private static void bounded(Map<String,Object> envelope) {
        // Exact compact size for the validated ASCII field schema. Raw request
        // byte limits (including whitespace) still belong to ProtocolJson.parse.
        long size=2+FIELDS.size()-1;
        for(String name:FIELDS) {
            Object value=envelope.get(name); size+=name.length()+3;
            if(name.equals("keyEpoch")) size+=Long.toString((Long)value).length();
            else {
                if(!(value instanceof String s) || s.length()>ProtocolJson.MAX_REQUEST) throw new ProtocolError();
                size+=s.length()+2;
            }
        }
        if(size>ProtocolJson.MAX_REQUEST) throw new ProtocolError();
    }
    private static byte[] key(byte[] root,Context expected,String format,byte[] salt) {
        List<Object> info=new ArrayList<>(List.of("cerberus-aead-key-v1",format)); info.addAll(expected.values());
        return Primitives.hkdf(root,salt,ProtocolJson.context(info),32);
    }
    private static byte[] aad(Context expected,String format,String salt) {
        List<Object> values=new ArrayList<>(List.of("cerberus-aead-v1",format)); values.addAll(expected.values()); values.add(salt);
        return ProtocolJson.context(values);
    }
    public static Map<String,Object> seal(byte[] root,Context expected,String format,byte[] plaintext,NonceGuard guard) {
        byte[] salt=new byte[32],nonce=new byte[12]; RANDOM.nextBytes(salt); RANDOM.nextBytes(nonce);
        return sealFixture(root,expected,format,plaintext,guard,salt,nonce);
    }
    public static Map<String,Object> sealFixture(byte[] root,Context expected,String format,byte[] plaintext,NonceGuard guard,byte[] salt,byte[] nonce) {
        expected(expected,format);
        byte[] rootSnapshot=material(root,32),saltSnapshot=material(salt,32),nonceSnapshot=material(nonce,12);
        byte[] plainSnapshot=plaintext==null?null:plaintext.clone();
        if(guard==null) throw new ProtocolError();
        guard.reserve(rootSnapshot,expected,saltSnapshot);
        if(plainSnapshot==null || plainSnapshot.length==0 || plainSnapshot.length>ProtocolJson.MAX_REQUEST) throw new ProtocolError();
        String encodedSalt=ProtocolJson.base64(saltSnapshot);
        byte[] combined=Primitives.gcmSeal(key(rootSnapshot,expected,format,saltSnapshot),nonceSnapshot,aad(expected,format,encodedSalt),plainSnapshot);
        Map<String,Object> envelope=Map.of("format",format,"keyEpoch",expected.keyEpoch(),"keySalt",encodedSalt,
            "nonce",ProtocolJson.base64(nonceSnapshot),"ciphertext",ProtocolJson.base64(Arrays.copyOf(combined,combined.length-16)),"tag",ProtocolJson.base64(Arrays.copyOfRange(combined,combined.length-16,combined.length)));
        bounded(envelope); return envelope;
    }
    public static byte[] open(byte[] root,Context expected,String format,Map<String,Object> envelope) {
        expected(expected,format); byte[] rootSnapshot=material(root,32);
        Map<String,Object> value=ProtocolJson.fields(envelope,FIELDS,Set.of());
        long epoch=ProtocolJson.integer(value.get("keyEpoch"),1,ProtocolJson.MAX_INTEGER);
        if(!format.equals(value.get("format")) || epoch!=expected.keyEpoch()) throw new ProtocolError();
        Map<String,Object> snapshot=new LinkedHashMap<>(value); snapshot.put("keyEpoch",epoch); bounded(snapshot);
        byte[] salt=ProtocolJson.unbase64((String)snapshot.get("keySalt"),32),nonce=ProtocolJson.unbase64((String)snapshot.get("nonce"),12),tag=ProtocolJson.unbase64((String)snapshot.get("tag"),16);
        String encoded=(String)snapshot.get("ciphertext");
        if(encoded.isEmpty()) throw new ProtocolError();
        byte[] ciphertext=ProtocolJson.unbase64(encoded,encoded.length()*3/4);
        byte[] combined=Arrays.copyOf(ciphertext,ciphertext.length+16); System.arraycopy(tag,0,combined,ciphertext.length,16);
        return Primitives.gcmOpen(key(rootSnapshot,expected,format,salt),nonce,aad(expected,format,(String)snapshot.get("keySalt")),combined);
    }
}
