package cerberus.protocol;

import java.lang.reflect.*;
import java.util.*;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.BiFunction;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;
import static cerberus.protocol.Fixtures.*;

class ProtectionTest {
    static final String OWNER="00000000-0000-0000-0000-000000000001", PROFILE="00000000-0000-0000-0000-000000000002", PASSWORD=" public É 🔐 ";
    Context slot=new Context(OWNER,"account-protection",OWNER,1,List.of()), recoverySlot=new Context(OWNER,"recovery",OWNER,1,List.of());
    byte[] salt=new byte[32],nonce=new byte[12],passwordSalt=new byte[16],secret=new byte[32];
    @BeforeEach void setup() {
        assertDoesNotThrow(()->Class.forName("cerberus.protocol.Protection"),"Protection feature missing");
        assertDoesNotThrow(()->Class.forName("cerberus.protocol.RecoveryBundle"),"RecoveryBundle feature missing");
        for(int i=0;i<32;i++) { salt[i]=(byte)i; secret[i]=(byte)(32+i); }
        for(int i=0;i<12;i++) nonce[i]=(byte)i;
        for(int i=0;i<16;i++) passwordSalt[i]=(byte)i;
    }
    Map<String,Object> validate(Map<String,Object> value,String scope,String id,Set<String> membership,Map<String,Object> verifier) {
        return map(call("Protection","validate",new Class<?>[]{Map.class,String.class,String.class,Set.class,Map.class},value,scope,id,membership,verifier));
    }
    Map<String,Object> wrap(String password,Map<String,Object> value,Context context) {
        return map(call("Protection","wrapFixture",new Class<?>[]{String.class,Map.class,Context.class,NonceGuard.class,byte[].class,byte[].class,byte[].class},password,value,context,new NonceGuard(0),salt,nonce,passwordSalt));
    }
    Map<String,Object> wrap() { return wrap(PASSWORD,bundle("account"),slot); }
    Map<String,Object> unwrap(String password,Map<String,Object> value,Context context,Set<String> membership,Map<String,Object> verifier) {
        return map(call("Protection","unwrap",new Class<?>[]{String.class,Map.class,Context.class,Set.class,Map.class},password,value,context,membership,verifier));
    }
    Map<String,Object> unwrap(Map<String,Object> value) { return unwrap(PASSWORD,value,slot,allowed("account"),publicJwk("unlock-account")); }
    Map<String,Object> recovery(long generation,String role) {
        return map(call("RecoveryBundle","wrapFixture",new Class<?>[]{byte[].class,Map.class,byte[].class,Context.class,long.class,NonceGuard.class,byte[].class,byte[].class},secret,bundle("account"),privateDer(role),recoverySlot,generation,new NonceGuard(0),salt,nonce));
    }
    Map<String,Object> recover(byte[] key,Map<String,Object> value,Context context,long generation,String role,Set<String> membership,String unlock) {
        return map(call("RecoveryBundle","unwrap",new Class<?>[]{byte[].class,Map.class,Context.class,long.class,Map.class,Set.class,Map.class},key,value,context,generation,publicJwk(role),membership,publicJwk(unlock)));
    }
    Map<String,Object> recover(Map<String,Object> value) { return recover(secret,value,recoverySlot,1,"recovery-1",allowed("account"),"unlock-account"); }
    Map<String,Object> copy(Map<String,Object> value) { return ProtocolJson.parse(Wire.encode(value),value.keySet(),Set.of(),1048576); }
    List<Object> roots(Map<String,Object> value) { return new ArrayList<>((List<?>)value.get("roots")); }
    @Test void GivenAccountAndProfileBundles_WhenValidatedAndWrapped_ThenScopedKeysRoundTrip() {
        for(String scope:List.of("account","profile")) {
            String id=scope.equals("account")?OWNER:PROFILE;
            Context context=new Context(OWNER,scope+"-protection",id,1,List.of());
            Map<String,Object> value=bundle(scope),key=publicJwk("unlock-"+scope);
            assertEquals(value,validate(value,scope,id,allowed(scope),key));
            assertEquals(value,unwrap(PASSWORD,wrap(PASSWORD,value,context),context,allowed(scope),key));
        }
    }
    @Test void GivenProfileSlot_WhenAccountRootIncluded_ThenRejected() {
        Map<String,Object> value=bundle("profile"); List<Object> roots=roots(value); roots.addFirst(roots(bundle("account")).getFirst()); value.put("roots",roots);
        rejected(()->validate(value,"profile",PROFILE,allowed("account"),publicJwk("unlock-profile")));
    }
    @Test void GivenInvalidRootsOrScope_WhenValidated_ThenRejected() {
        List<Map<String,Object>> values=new ArrayList<>();
        Map<String,Object> empty=bundle("account"); empty.put("roots",List.of()); values.add(empty);
        Map<String,Object> reversed=bundle("account"); List<Object> rs=roots(reversed); Collections.reverse(rs); reversed.put("roots",rs); values.add(reversed);
        Map<String,Object> duplicate=bundle("account"); Object root=roots(duplicate).getFirst(); duplicate.put("roots",List.of(root,root)); values.add(duplicate);
        for(var change:Map.<String,Object>of("format","other","scopeKind","profile","scopeId",PROFILE,"unlockPrivateKey","AA","extra",1L).entrySet()) {
            Map<String,Object> value=bundle("account"); value.put(change.getKey(),change.getValue()); values.add(value);
        }
        Map<String,Object> missing=bundle("account"); missing.remove("roots"); values.add(missing);
        for(var value:values) rejected(()->validate(value,"account",OWNER,allowed("account"),publicJwk("unlock-account")));
    }
    @Test void GivenChangedRootFields_WhenValidated_ThenRejected() {
        for(var change:Map.<String,Object>of("resourceKind","unknown","resourceId","bad","keyEpoch",true,"key","AA","extra",1L).entrySet()) {
            Map<String,Object> value=bundle("account"); map(roots(value).getFirst()).put(change.getKey(),change.getValue());
            rejected(()->validate(value,"account",OWNER,allowed("account"),publicJwk("unlock-account")));
        }
    }
    @Test void GivenUnauthorizedRootOrUnlockKey_WhenValidated_ThenRejected() {
        rejected(()->validate(bundle("account"),"account",OWNER,allowed("profile"),publicJwk("unlock-account")));
        rejected(()->validate(bundle("account"),"account",OWNER,allowed("account"),publicJwk("unlock-profile")));
        Map<String,Object> value=bundle("profile"); map(roots(value).getFirst()).put("resourceId",OWNER);
        rejected(()->validate(value,"profile",PROFILE,allowed("account"),publicJwk("unlock-profile")));
    }
    void rejectBeforeKdf(Map<String,Object> wrapper) {
        AtomicInteger calls=new AtomicInteger();
        BiFunction<byte[],byte[],byte[]> derive=(password,s)-> { calls.incrementAndGet(); return new byte[32]; };
        rejected(()-> {
            try {
                var method=Class.forName("cerberus.protocol.Protection").getDeclaredMethod("unwrapCore",String.class,Map.class,Context.class,Set.class,Map.class,BiFunction.class);
                method.setAccessible(true); method.invoke(null,PASSWORD,wrapper,slot,allowed("account"),publicJwk("unlock-account"),derive);
            } catch(InvocationTargetException ex) { throw (RuntimeException)ex.getCause(); }
            catch(ReflectiveOperationException ex) { throw new AssertionError(ex); }
        });
        assertEquals(0,calls.get(),"invalid public metadata must not invoke KDF");
    }
    @Test void GivenChangedKdf_WhenUnwrapped_ThenNoDerivation() {
        Map<String,Object> wrapped=wrap();
        for(var change:Map.<String,Object>of("algorithm","argon2i","memoryKiB",1L<<40,"iterations",4L,"parallelism",1L,"salt","AA","extra",1L).entrySet()) {
            Map<String,Object> value=copy(wrapped); map(value.get("kdf")).put(change.getKey(),change.getValue()); rejectBeforeKdf(value);
        }
        Map<String,Object> value=copy(wrapped); map(value.get("kdf")).put("memoryKiB",true); rejectBeforeKdf(value);
    }
    @Test void GivenMalformedEnvelope_WhenUnwrapped_ThenNoDerivation() {
        Map<String,Object> wrapped=wrap();
        for(var change:Map.<String,Object>of("keyEpoch",true,"format","unknown","keySalt","AA","nonce","AA","ciphertext","A","tag","AA","extra",1L).entrySet()) {
            Map<String,Object> value=copy(wrapped); value.put(change.getKey(),change.getValue()); rejectBeforeKdf(value);
        }
        Map<String,Object> oversized=copy(wrapped); oversized.put("ciphertext","A".repeat(1048576)); rejectBeforeKdf(oversized);
        Map<String,Object> missing=copy(wrapped); map(missing.get("kdf")).remove("salt"); rejectBeforeKdf(missing);
        Map<String,Object> epoch=copy(wrapped); epoch.put("keyEpoch",2L); rejectBeforeKdf(epoch);
        Map<String,Object> noTag=copy(wrapped); noTag.remove("tag"); rejectBeforeKdf(noTag);
    }
    @Test void GivenExactUnicodePassword_WhenChangedWithoutNormalization_ThenReject() {
        for(var pair:List.of(List.of("é","e\u0301"),List.of(" secret ","secret"),List.of("Secret","secret"),List.of("🔐","🔑"))) {
            var value=wrap(pair.getFirst(),bundle("account"),slot);
            assertEquals(bundle("account"),unwrap(pair.getFirst(),value,slot,allowed("account"),publicJwk("unlock-account")));
            rejected(()->unwrap(pair.getLast(),value,slot,allowed("account"),publicJwk("unlock-account")));
        }
        assertFalse(Arrays.equals(Primitives.argon2(ProtocolJson.utf8("é"),passwordSalt),Primitives.argon2(ProtocolJson.utf8("e\u0301"),passwordSalt)));
        rejected(()->wrap("\ud800",bundle("account"),slot));
    }
    @Test void GivenChangedPasswordSaltOrCiphertext_WhenUnwrapped_ThenReject() {
        Map<String,Object> wrapped=wrap();
        for(String key:List.of("keySalt","nonce","tag","ciphertext","passwordSalt")) {
            Map<String,Object> value=copy(wrapped);
            if(key.equals("passwordSalt")) { byte[] s=new byte[16]; Arrays.fill(s,(byte)1); map(value.get("kdf")).put("salt",ProtocolJson.base64(s)); }
            else { String original=(String)value.get(key); value.put(key,(original.startsWith("A")?"B":"A")+original.substring(1)); }
            rejected(()->unwrap(value));
        }
    }
    @Test void GivenAuthenticatedBundle_WhenMembershipOrVerifierWrong_ThenNoKeysReturned() {
        var value=wrap();
        rejected(()->unwrap(PASSWORD,value,slot,allowed("profile"),publicJwk("unlock-account")));
        rejected(()->unwrap(PASSWORD,value,slot,allowed("account"),publicJwk("author")));
    }
    @Test void GivenRewrap_WhenSlotEpochIncreased_ThenContentRootsUnchanged() {
        var original=unwrap(wrap()); Context rotated=new Context(OWNER,"account-protection",OWNER,2,List.of());
        var value=wrap("new password",original,rotated); assertEquals(2L,value.get("keyEpoch"));
        assertEquals(original,unwrap("new password",value,rotated,allowed("account"),publicJwk("unlock-account")));
        rejected(()->unwrap("new password",value,slot,allowed("account"),publicJwk("unlock-account")));
    }
    @Test void GivenNormalWrapping_WhenRepeated_ThenAllSaltsAndNoncesFresh() {
        Class<?>[] types={String.class,Map.class,Context.class,NonceGuard.class};
        var a=map(call("Protection","wrap",types,"pwd",bundle("account"),slot,new NonceGuard(0)));
        var b=map(call("Protection","wrap",types,"pwd",bundle("account"),slot,new NonceGuard(0)));
        assertNotEquals(a.get("keySalt"),b.get("keySalt")); assertNotEquals(a.get("nonce"),b.get("nonce"));
        assertNotEquals(map(a.get("kdf")).get("salt"),map(b.get("kdf")).get("salt"));
    }
    @Test void GivenRecoverySecret_WhenWrapped_ThenAccountAndProofRoundTrip() {
        var value=recover(recovery(1,"recovery-1"));
        assertEquals(bundle("account"),value.get("protectionBundle")); assertEquals(1L,value.get("generation"));
        assertEquals(ProtocolJson.base64(privateDer("recovery-1")),value.get("proofPrivateKey"));
    }
    @Test void GivenChangedRecoveryBindings_WhenUnwrapped_ThenReject() {
        var value=recovery(1,"recovery-1");
        rejected(()->recover(new byte[32],value,recoverySlot,1,"recovery-1",allowed("account"),"unlock-account"));
        rejected(()->recover(ProtocolJson.unbase64((String)publicJwk("recovery-1").get("x"),32),value,recoverySlot,1,"recovery-1",allowed("account"),"unlock-account"));
        rejected(()->recover(secret,value,recoverySlot,2,"recovery-1",allowed("account"),"unlock-account"));
        rejected(()->recover(secret,value,recoverySlot,1,"recovery-2",allowed("account"),"unlock-account"));
        rejected(()->recover(secret,value,new Context(PROFILE,"recovery",OWNER,1,List.of()),1,"recovery-1",allowed("account"),"unlock-account"));
        rejected(()->recover(secret,value,recoverySlot,1,"recovery-1",allowed("profile"),"unlock-account"));
        rejected(()->recover(secret,value,recoverySlot,1,"recovery-1",allowed("account"),"unlock-profile"));
        for(var change:Map.<String,Object>of("generation",true,"proofKeyFingerprint",Keys.thumbprint(publicJwk("recovery-2")),"extra",1L).entrySet()) {
            var changed=copy(value); changed.put(change.getKey(),change.getValue()); rejected(()->recover(changed));
        }
    }
    @Test void GivenNewRecoveryGeneration_WhenWrapped_ThenIndependentProofKeyBinds() {
        var value=recovery(2,"recovery-2"); assertNotEquals(Keys.thumbprint(publicJwk("recovery-1")),value.get("proofKeyFingerprint"));
        assertEquals(2L,recover(secret,value,recoverySlot,2,"recovery-2",allowed("account"),"unlock-account").get("generation"));
        rejected(()->recover(value));
    }
    @Test void GivenProfileBundleOrBadSlot_WhenRecoveryWrapped_ThenRejected() {
        Class<?>[] types={byte[].class,Map.class,byte[].class,Context.class,long.class,NonceGuard.class};
        rejected(()->call("RecoveryBundle","wrap",types,secret,bundle("profile"),privateDer("recovery-1"),recoverySlot,1L,new NonceGuard(0)));
        for(Context context:List.of(slot,new Context(OWNER,"recovery",PROFILE,1,List.of()),new Context(OWNER,"recovery",OWNER,1,List.of(1L))))
            rejected(()->call("RecoveryBundle","wrap",types,secret,bundle("account"),privateDer("recovery-1"),context,1L,new NonceGuard(0)));
    }
    @Test void GivenAuthenticatedInvalidPasswordPlaintext_WhenUnwrapped_ThenReject() {
        Map<String,Object> kdf=Map.of("algorithm","argon2id-v1.3","memoryKiB",65536L,"iterations",3L,"parallelism",4L,"salt",ProtocolJson.base64(passwordSalt));
        Context expected=new Context(OWNER,"account-protection",OWNER,1,List.of("argon2id-v1.3",65536L,3L,4L,kdf.get("salt")));
        byte[] root=Primitives.argon2(ProtocolJson.utf8(PASSWORD),passwordSalt); List<byte[]> invalid=new ArrayList<>();
        for(var change:Map.<String,Object>of("unlockPrivateKey",ProtocolJson.base64(privateDer("unlock-profile")),"roots",List.of(),"scopeId",PROFILE,"extra",1L).entrySet()) {
            var inner=bundle("account"); inner.put(change.getKey(),change.getValue()); invalid.add(Wire.encode(inner));
        }
        invalid.add(ProtocolJson.utf8("{\"format\":\"one\",\"format\":\"two\"}"));
        invalid.add(ProtocolJson.utf8("\ufeff{}")); invalid.add(ProtocolJson.utf8("{\"format\":1.0}"));
        for(byte[] raw:invalid) {
            var value=new LinkedHashMap<>(Symmetric.sealBound(root,expected,"cerberus-password-wrap-v1",raw,new NonceGuard(0),salt,nonce)); value.put("kdf",kdf);
            rejected(()->unwrap(value));
        }
    }
    @Test void GivenAuthenticatedInvalidRecoveryPlaintext_WhenUnwrapped_ThenReject() {
        String fingerprint=Keys.thumbprint(publicJwk("recovery-1"));
        Context expected=new Context(OWNER,"recovery",OWNER,1,List.of(1L,fingerprint));
        for(var change:Map.<String,Object>of("generation",2L,"proofPrivateKey",ProtocolJson.base64(privateDer("recovery-2")),"protectionBundle",bundle("profile"),"extra",1L).entrySet()) {
            var inner=new LinkedHashMap<String,Object>(Map.of("format","cerberus-recovery-bundle-v1","generation",1L,"proofPrivateKey",ProtocolJson.base64(privateDer("recovery-1")),"protectionBundle",bundle("account")));
            inner.put(change.getKey(),change.getValue());
            var value=new LinkedHashMap<>(Symmetric.sealBound(secret,expected,"cerberus-recovery-wrap-v1",Wire.encode(inner),new NonceGuard(0),salt,nonce));
            value.put("generation",1L); value.put("proofKeyFingerprint",fingerprint); rejected(()->recover(value));
        }
    }
}
