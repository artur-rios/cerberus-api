package cerberus.protocol;

import java.util.*;

/** Client-side recovery composition. The server retains only public verifiers. */
public final class RecoveryBundle {
    private RecoveryBundle() {}
    static final String FORMAT="cerberus-recovery-wrap-v1",BUNDLE="cerberus-recovery-bundle-v1";
    static final Set<String> WRAPPER_FIELDS=Set.of("format","keyEpoch","keySalt","nonce","ciphertext","tag","generation","proofKeyFingerprint");
    static final Set<String> BUNDLE_FIELDS=Set.of("format","generation","proofPrivateKey","protectionBundle");
    private static void slot(Context slot) {
        if(slot==null || !slot.resourceKind().equals("recovery") || !slot.resourceId().equals(slot.ownerId()) || !slot.extraContext().isEmpty()) throw new ProtocolError();
    }
    private static byte[] secret(byte[] value) { if(value==null || value.length!=32) throw new ProtocolError(); return value.clone(); }
    private static Context context(Context slot,long generation,String fingerprint) {
        return new Context(slot.ownerId(),slot.resourceKind(),slot.resourceId(),slot.keyEpoch(),List.of(generation,fingerprint));
    }
    public static Map<String,Object> wrap(byte[] secret,Map<String,Object> accountBundle,byte[] proofPrivateDer,Context slot,long generation,NonceGuard guard) {
        return wrapFixture(secret,accountBundle,proofPrivateDer,slot,generation,guard,Protection.random(32),Protection.random(12));
    }
    public static Map<String,Object> wrapFixture(byte[] secret,Map<String,Object> accountBundle,byte[] proofPrivateDer,Context slot,long generation,NonceGuard guard,byte[] keySalt,byte[] nonce) {
        slot(slot); ProtocolJson.integer(generation,1,ProtocolJson.MAX_INTEGER);
        byte[] root=secret(secret);
        try {
            Map<String,Object> account=Protection.structure(accountBundle,"account",slot.resourceId());
            String fingerprint=Keys.thumbprint(Keys.publicJwk(proofPrivateDer));
            Map<String,Object> bundle=Map.of("format",BUNDLE,"generation",generation,"proofPrivateKey",ProtocolJson.base64(proofPrivateDer),"protectionBundle",account);
            Map<String,Object> result=new LinkedHashMap<>(Symmetric.sealBound(root,context(slot,generation,fingerprint),FORMAT,Wire.encode(bundle),guard,keySalt,nonce));
            result.put("generation",generation); result.put("proofKeyFingerprint",fingerprint); Wire.encode(result); return result;
        } finally { Arrays.fill(root,(byte)0); }
    }
    public static Map<String,Object> unwrap(byte[] secret,Map<String,Object> wrapper,Context slot,long generation,Map<String,Object> recoveryJwk,Set<String> allowedRoots,Map<String,Object> unlockJwk) {
        slot(slot); ProtocolJson.integer(generation,1,ProtocolJson.MAX_INTEGER);
        byte[] root=secret(secret);
        try {
            Map<String,Object> value=Protection.snapshot(wrapper,WRAPPER_FIELDS);
            String fingerprint=Keys.thumbprint(recoveryJwk);
            ProtocolJson.unbase64(ProtocolJson.text(value.get("proofKeyFingerprint"),true),32);
            if(ProtocolJson.integer(value.get("generation"),1,ProtocolJson.MAX_INTEGER)!=generation || !fingerprint.equals(value.get("proofKeyFingerprint"))) throw new ProtocolError();
            Context expected=context(slot,generation,fingerprint); Map<String,Object> envelope=Protection.envelope(value);
            Symmetric.validateBound(expected,FORMAT,envelope);
            byte[] raw=Symmetric.openBound(root,expected,FORMAT,envelope);
            try {
                Map<String,Object> bundle=ProtocolJson.parse(raw,BUNDLE_FIELDS,Set.of(),ProtocolJson.MAX_REQUEST);
                if(!BUNDLE.equals(bundle.get("format")) || ProtocolJson.integer(bundle.get("generation"),1,ProtocolJson.MAX_INTEGER)!=generation) throw new ProtocolError();
                if(!Keys.thumbprint(Keys.publicJwk(Protection.privateBytes(bundle.get("proofPrivateKey")))).equals(fingerprint)) throw new ProtocolError();
                Map<String,Object> account=ProtocolJson.fields(bundle.get("protectionBundle"),Protection.BUNDLE_FIELDS,Set.of());
                bundle.put("protectionBundle",Protection.validate(account,"account",slot.resourceId(),allowedRoots,unlockJwk)); return bundle;
            } finally { Arrays.fill(raw,(byte)0); }
        } finally { Arrays.fill(root,(byte)0); }
    }
}
