package cerberus.protocol;

import java.security.SecureRandom;
import java.util.*;
import java.util.function.BiFunction;

/** Client-side scoped password bundle; not a server password verifier. */
public final class Protection {
    private Protection() {}
    static final String FORMAT="cerberus-password-wrap-v1", BUNDLE="cerberus-protection-bundle-v1";
    static final Set<String> ENVELOPE_FIELDS=Set.of("format","keyEpoch","keySalt","nonce","ciphertext","tag");
    static final Set<String> BUNDLE_FIELDS=Set.of("format","scopeKind","scopeId","roots","unlockPrivateKey");
    static final Set<String> WRAPPER_FIELDS=Set.of("format","keyEpoch","keySalt","nonce","ciphertext","tag","kdf");
    private static final Set<String> ROOT_FIELDS=Set.of("resourceKind","resourceId","keyEpoch","key");
    private static final Set<String> KINDS=Set.of("account","profile","record","folder","collection");
    private static final SecureRandom RANDOM=new SecureRandom();
    static byte[] random(int length) { byte[] value=new byte[length]; RANDOM.nextBytes(value); return value; }
    static Map<String,Object> snapshot(Object value,Set<String> schema) {
        return ProtocolJson.parse(Wire.encode(ProtocolJson.fields(value,schema,Set.of())),schema,Set.of(),ProtocolJson.MAX_REQUEST);
    }
    static byte[] privateBytes(Object value) {
        String encoded=ProtocolJson.text(value,true); return ProtocolJson.unbase64(encoded,encoded.length()*3/4);
    }
    static Map<String,Object> structure(Map<String,Object> bundle,String scopeKind,String scopeId) {
        Map<String,Object> value=snapshot(bundle,BUNDLE_FIELDS);
        ProtocolJson.guid(scopeId);
        if(scopeKind==null || !Set.of("account","profile").contains(scopeKind) || !BUNDLE.equals(value.get("format")) || !scopeKind.equals(value.get("scopeKind")) || !scopeId.equals(value.get("scopeId"))) throw new ProtocolError();
        if(!(value.get("roots") instanceof List<?> roots) || roots.isEmpty()) throw new ProtocolError();
        String previousKind=null,previousId=null;
        for(Object entry:roots) {
            Map<String,Object> root=ProtocolJson.fields(entry,ROOT_FIELDS,Set.of());
            String kind=ProtocolJson.text(root.get("resourceKind"),true),id=ProtocolJson.guid(ProtocolJson.text(root.get("resourceId"),true));
            if(!KINDS.contains(kind)) throw new ProtocolError();
            ProtocolJson.integer(root.get("keyEpoch"),1,ProtocolJson.MAX_INTEGER);
            ProtocolJson.unbase64(ProtocolJson.text(root.get("key"),true),32);
            if(previousKind!=null && (kind.compareTo(previousKind)<0 || kind.equals(previousKind) && id.compareTo(previousId)<=0)) throw new ProtocolError();
            previousKind=kind; previousId=id;
            if(kind.equals("account") && (!scopeKind.equals("account") || !id.equals(scopeId))) throw new ProtocolError();
            if(scopeKind.equals("profile") && kind.equals("profile") && !id.equals(scopeId)) throw new ProtocolError();
        }
        Keys.publicJwk(privateBytes(value.get("unlockPrivateKey")));
        return value;
    }
    public static Map<String,Object> validate(Map<String,Object> bundle,String scopeKind,String scopeId,Set<String> allowedRoots,Map<String,Object> unlockJwk) {
        Map<String,Object> value=structure(bundle,scopeKind,scopeId);
        if(allowedRoots==null) throw new ProtocolError();
        for(Object entry:allowedRoots) if(!(entry instanceof String)) throw new ProtocolError();
        for(Object entry:(List<?>)value.get("roots")) {
            Map<String,Object> root=ProtocolJson.fields(entry,ROOT_FIELDS,Set.of());
            if(!allowedRoots.contains(root.get("resourceKind")+":"+root.get("resourceId"))) throw new ProtocolError();
        }
        if(!Keys.thumbprint(Keys.publicJwk(privateBytes(value.get("unlockPrivateKey")))).equals(Keys.thumbprint(unlockJwk))) throw new ProtocolError();
        return value;
    }
    private static String slot(Context slot) {
        if(slot==null || !slot.extraContext().isEmpty() || !Set.of("account-protection","profile-protection").contains(slot.resourceKind())) throw new ProtocolError();
        String scope=slot.resourceKind().equals("account-protection")?"account":"profile";
        if(scope.equals("account") && !slot.resourceId().equals(slot.ownerId())) throw new ProtocolError();
        return scope;
    }
    private static byte[] kdf(Map<String,Object> kdf) {
        ProtocolJson.fields(kdf,Set.of("algorithm","memoryKiB","iterations","parallelism","salt"),Set.of());
        if(!"argon2id-v1.3".equals(kdf.get("algorithm")) || ProtocolJson.integer(kdf.get("memoryKiB"),1,ProtocolJson.MAX_INTEGER)!=65536 || ProtocolJson.integer(kdf.get("iterations"),1,ProtocolJson.MAX_INTEGER)!=3 || ProtocolJson.integer(kdf.get("parallelism"),1,ProtocolJson.MAX_INTEGER)!=4) throw new ProtocolError();
        return ProtocolJson.unbase64(ProtocolJson.text(kdf.get("salt"),true),16);
    }
    private static Context context(Context slot,Map<String,Object> kdf) {
        return new Context(slot.ownerId(),slot.resourceKind(),slot.resourceId(),slot.keyEpoch(),List.of("argon2id-v1.3",65536L,3L,4L,kdf.get("salt")));
    }
    static Map<String,Object> envelope(Map<String,Object> wrapper) {
        Map<String,Object> value=new LinkedHashMap<>(); for(String field:ENVELOPE_FIELDS) value.put(field,wrapper.get(field)); return value;
    }
    public static Map<String,Object> wrap(String password,Map<String,Object> bundle,Context slot,NonceGuard guard) {
        return wrapFixture(password,bundle,slot,guard,random(32),random(12),random(16));
    }
    public static Map<String,Object> wrapFixture(String password,Map<String,Object> bundle,Context slot,NonceGuard guard,byte[] keySalt,byte[] nonce,byte[] passwordSalt) {
        String scope=slot(slot); Map<String,Object> value=structure(bundle,scope,slot.resourceId());
        byte[] passwordBytes=ProtocolJson.utf8(password);
        Map<String,Object> kdf=Map.of("algorithm","argon2id-v1.3","memoryKiB",65536L,"iterations",3L,"parallelism",4L,"salt",ProtocolJson.base64(passwordSalt));
        byte[] validatedSalt=kdf(kdf),root=Primitives.argon2(passwordBytes,validatedSalt);
        try {
            Map<String,Object> result=new LinkedHashMap<>(Symmetric.sealBound(root,context(slot,kdf),FORMAT,Wire.encode(value),guard,keySalt,nonce));
            result.put("kdf",kdf); Wire.encode(result); return result;
        } finally { Arrays.fill(root,(byte)0); Arrays.fill(passwordBytes,(byte)0); }
    }
    public static Map<String,Object> unwrap(String password,Map<String,Object> wrapper,Context slot,Set<String> allowedRoots,Map<String,Object> unlockJwk) {
        return unwrapCore(password,wrapper,slot,allowedRoots,unlockJwk,Primitives::argon2);
    }
    static Map<String,Object> unwrapCore(String password,Map<String,Object> wrapper,Context slot,Set<String> allowedRoots,Map<String,Object> unlockJwk,BiFunction<byte[],byte[],byte[]> derive) {
        String scope=slot(slot); Map<String,Object> value=snapshot(wrapper,WRAPPER_FIELDS);
        Map<String,Object> kdf=ProtocolJson.fields(value.get("kdf"),Set.of("algorithm","memoryKiB","iterations","parallelism","salt"),Set.of());
        byte[] passwordSalt=kdf(kdf); Context expected=context(slot,kdf);
        Map<String,Object> envelope=envelope(value); Symmetric.validateBound(expected,FORMAT,envelope);
        byte[] passwordBytes=ProtocolJson.utf8(password),root=derive.apply(passwordBytes,passwordSalt);
        try {
            byte[] raw=Symmetric.openBound(root,expected,FORMAT,envelope);
            try { return validate(ProtocolJson.parse(raw,BUNDLE_FIELDS,Set.of(),ProtocolJson.MAX_REQUEST),scope,slot.resourceId(),allowedRoots,unlockJwk); }
            finally { Arrays.fill(raw,(byte)0); }
        } finally { Arrays.fill(root,(byte)0); Arrays.fill(passwordBytes,(byte)0); }
    }
}
