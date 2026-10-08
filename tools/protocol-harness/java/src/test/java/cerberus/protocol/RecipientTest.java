package cerberus.protocol;

import java.lang.reflect.*;
import java.util.*;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.BiFunction;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;
import static cerberus.protocol.Fixtures.*;

class RecipientTest {
    static final String OWNER="00000000-0000-0000-0000-000000000001",RESOURCE="00000000-0000-0000-0000-000000000002",GRANT="00000000-0000-0000-0000-000000000003",IDENTITY="00000000-0000-0000-0000-000000000004",OTHER="00000000-0000-0000-0000-000000000005";
    Context resource=new Context(OWNER,"record",RESOURCE,1,List.of()); byte[] root=new byte[32]; Object trust; Class<?> trustClass;
    @BeforeEach void setup() {
        assertDoesNotThrow(()->Class.forName("cerberus.protocol.Recipient"),"Recipient feature missing");
        trustClass=assertDoesNotThrow(()->Class.forName("cerberus.protocol.ClientTrust"),"ClientTrust feature missing");
        trust=trust(publicJwk("recipient"),publicJwk("author")); for(int i=0;i<32;i++) root[i]=(byte)i;
    }
    Object trust(Map<String,Object> recipient,Map<String,Object> author) {
        try { return trustClass.getConstructor(String.class,Map.class,Map.class,long.class).newInstance(OWNER,recipient,author,1L); }
        catch(InvocationTargetException ex) { throw (RuntimeException)ex.getCause(); }
        catch(ReflectiveOperationException ex) { throw new AssertionError(ex); }
    }
    Map<String,Object> wrap(byte[] key,Context context,String grant,long revision,String identity,Map<String,Object> recipient,byte[] author) {
        return map(call("Recipient","wrap",new Class<?>[]{byte[].class,Context.class,String.class,long.class,String.class,Map.class,byte[].class},key,context,grant,revision,identity,recipient,author));
    }
    Map<String,Object> wrap() { return wrap(root,resource,GRANT,1,IDENTITY,publicJwk("recipient"),privateDer("author")); }
    byte[] open(Map<String,Object> envelope,Context context,String grant,long revision,String identity,byte[] privateKey,Object pins) {
        return (byte[])call("Recipient","open",new Class<?>[]{Map.class,Context.class,String.class,long.class,String.class,byte[].class,trustClass},envelope,context,grant,revision,identity,privateKey,pins);
    }
    byte[] open(Map<String,Object> envelope) { return open(envelope,resource,GRANT,1,IDENTITY,privateDer("recipient"),trust); }
    void transition(String account,String role,Map<String,Object> key,long revision,byte[] signature) {
        try { trustClass.getMethod("transition",String.class,String.class,Map.class,long.class,byte[].class).invoke(trust,account,role,key,revision,signature); }
        catch(InvocationTargetException ex) { throw (RuntimeException)ex.getCause(); }
        catch(ReflectiveOperationException ex) { throw new AssertionError(ex); }
    }
    byte[] transitionSignature(String account,String role,String oldRole,String newRole,long revision,String signer) {
        return Keys.sign(privateDer(signer),ProtocolJson.context(List.of("cerberus-key-transition-v1",account,role,Keys.thumbprint(publicJwk(oldRole)),Keys.thumbprint(publicJwk(newRole)),revision)));
    }
    byte[] signed(Map<String,Object> value) {
        byte[] info=ProtocolJson.context(List.of("cerberus-recipient-wrap-v1",OWNER,"record",RESOURCE,1L,GRANT,1L,IDENTITY,Keys.thumbprint(publicJwk("recipient")),Keys.thumbprint(publicJwk("author"))));
        return ProtocolJson.context(List.of("cerberus-recipient-signature-v1",ProtocolJson.base64(info),value.get("enc"),value.get("ciphertext")));
    }
    void rejectBeforeDecap(Map<String,Object> value,Context context,String grant,long revision,String identity) {
        AtomicInteger calls=new AtomicInteger(); BiFunction<byte[],byte[],byte[]> decap=(sealed,info)-> { calls.incrementAndGet(); return root; };
        rejected(()-> {
            try {
                var method=Class.forName("cerberus.protocol.Recipient").getDeclaredMethod("openCore",Map.class,Context.class,String.class,long.class,String.class,byte[].class,trustClass,BiFunction.class);
                method.setAccessible(true); method.invoke(null,value,context,grant,revision,identity,privateDer("recipient"),trust,decap);
            } catch(InvocationTargetException ex) { throw (RuntimeException)ex.getCause(); }
            catch(ReflectiveOperationException ex) { throw new AssertionError(ex); }
        }); assertEquals(0,calls.get());
    }
    @Test void GivenPinnedAuthorAndRecipient_WhenWrapped_ThenOnlyRecipientOpens() {
        var value=wrap(); assertEquals(65,ProtocolJson.unbase64((String)value.get("enc"),65).length); assertEquals(48,ProtocolJson.unbase64((String)value.get("ciphertext"),48).length);
        assertArrayEquals(root,open(value)); assertTrue(Keys.verify(publicJwk("author"),signed(value),ProtocolJson.unbase64((String)value.get("signature"),64)));
        rejected(()->open(value,resource,GRANT,1,IDENTITY,privateDer("author"),trust));
    }
    @Test void GivenChangedTrustedBinding_WhenOpened_ThenRejectBeforeDecapsulation() {
        var value=wrap();
        for(Context context:List.of(new Context(OTHER,"record",RESOURCE,1,List.of()),new Context(OWNER,"folder",RESOURCE,1,List.of()),new Context(OWNER,"record",OTHER,1,List.of()),new Context(OWNER,"record",RESOURCE,2,List.of()),new Context(OWNER,"record",RESOURCE,1,List.of("extra"))))
            rejectBeforeDecap(value,context,GRANT,1,IDENTITY);
        rejectBeforeDecap(value,resource,OTHER,1,IDENTITY); rejectBeforeDecap(value,resource,GRANT,2,IDENTITY); rejectBeforeDecap(value,resource,GRANT,1,OTHER);
    }
    @Test void GivenChangedEnvelopeMetadataOrSignature_WhenOpened_ThenRejectBeforeDecapsulation() {
        var value=wrap();
        Map<String,Object> changes=new LinkedHashMap<>(Map.of("format","other","keyEpoch",2L,"grantId",OTHER,"grantRevision",true,"recipientIdentityId",OTHER,"recipientKeyFingerprint",Keys.thumbprint(publicJwk("author")),"authorKeyFingerprint",Keys.thumbprint(publicJwk("recipient")),"signature",ProtocolJson.base64(new byte[64]),"extra",1L));
        for(var change:changes.entrySet()) { var changed=new LinkedHashMap<>(value); changed.put(change.getKey(),change.getValue()); rejectBeforeDecap(changed,resource,GRANT,1,IDENTITY); }
        var changed=new LinkedHashMap<>(value); changed.put("signature","AA"); rejectBeforeDecap(changed,resource,GRANT,1,IDENTITY);
    }
    @Test void GivenUnpinnedAuthorOrSuppliedJwk_WhenOpened_ThenReject() {
        var forged=wrap(root,resource,GRANT,1,IDENTITY,publicJwk("recipient"),privateDer("lease")); rejectBeforeDecap(forged,resource,GRANT,1,IDENTITY);
        forged=new LinkedHashMap<>(forged); forged.put("authorJwk",publicJwk("lease")); var supplied=forged; rejected(()->open(supplied));
    }
    @Test void GivenMalformedOrCorruptedHpkeBytes_WhenOpened_ThenReject() {
        var original=wrap(); List<Map.Entry<String,byte[]>> mutations=new ArrayList<>();
        mutations.add(Map.entry("enc",new byte[65])); byte[] zeroPoint=new byte[65]; zeroPoint[0]=4; mutations.add(Map.entry("enc",zeroPoint));
        byte[] compressed=ProtocolJson.unbase64((String)original.get("enc"),65); compressed[0]=2; mutations.add(Map.entry("enc",compressed));
        mutations.add(Map.entry("enc",new byte[64])); mutations.add(Map.entry("ciphertext",new byte[48])); mutations.add(Map.entry("ciphertext",new byte[47]));
        for(var change:mutations) {
            var value=new LinkedHashMap<>(original); value.put(change.getKey(),ProtocolJson.base64(change.getValue())); value.put("signature",ProtocolJson.base64(Keys.sign(privateDer("author"),signed(value)))); rejected(()->open(value));
        }
        var missing=new LinkedHashMap<>(original); missing.remove("enc"); rejected(()->open(missing));
    }
    @Test void GivenInvalidInputsOrRoleReuse_WhenWrapped_ThenReject() {
        rejected(()->wrap(new byte[31],resource,GRANT,1,IDENTITY,publicJwk("recipient"),privateDer("author")));
        rejected(()->wrap(root,resource,GRANT,0,IDENTITY,publicJwk("recipient"),privateDer("author")));
        rejected(()->wrap(root,resource,"bad",1,IDENTITY,publicJwk("recipient"),privateDer("author")));
        rejected(()->wrap(root,resource,GRANT,1,"bad",publicJwk("recipient"),privateDer("author")));
        rejected(()->wrap(root,new Context(OWNER,"recovery",RESOURCE,1,List.of()),GRANT,1,IDENTITY,publicJwk("recipient"),privateDer("author")));
        rejected(()->wrap(root,resource,GRANT,1,IDENTITY,publicJwk("author"),privateDer("author")));
        rejected(()->wrap(root,resource,GRANT,1,IDENTITY,Map.of("crv","P-256","kty","EC","x",ProtocolJson.base64(new byte[32]),"y",ProtocolJson.base64(new byte[32])),privateDer("author")));
    }
    @Test void GivenNormalHpkeWrapping_WhenRepeated_ThenFreshEncapsulation() {
        var a=wrap(); var b=wrap(); assertNotEquals(a.get("enc"),b.get("enc")); assertNotEquals(a.get("ciphertext"),b.get("ciphertext")); assertArrayEquals(root,open(a)); assertArrayEquals(root,open(b));
    }
    @Test void GivenOldAuthorTransition_WhenVerified_ThenNewRecipientPinOpens() {
        transition(OWNER,"recipient-kem",publicJwk("recovery-1"),2,transitionSignature(OWNER,"recipient-kem","recipient","recovery-1",2,"author"));
        var value=wrap(root,resource,GRANT,1,IDENTITY,publicJwk("recovery-1"),privateDer("author"));
        assertArrayEquals(root,open(value,resource,GRANT,1,IDENTITY,privateDer("recovery-1"),trust)); rejected(()->open(wrap()));
    }
    @Test void GivenAuthorTransition_WhenVerified_ThenOnlyNewAuthorCanSignNextTransition() {
        transition(OWNER,"envelope-author",publicJwk("lease"),2,transitionSignature(OWNER,"envelope-author","author","lease",2,"author"));
        assertArrayEquals(root,open(wrap(root,resource,GRANT,1,IDENTITY,publicJwk("recipient"),privateDer("lease")))); rejected(()->open(wrap()));
        rejected(()->transition(OWNER,"recipient-kem",publicJwk("recovery-2"),3,transitionSignature(OWNER,"recipient-kem","recipient","recovery-2",3,"author")));
        transition(OWNER,"recipient-kem",publicJwk("recovery-2"),3,transitionSignature(OWNER,"recipient-kem","recipient","recovery-2",3,"lease"));
    }
    @Test void GivenSelfSignedWrongAccountOrRoleTransition_WhenSubmitted_ThenPinsUnchanged() {
        var saved=wrap(); byte[] valid=transitionSignature(OWNER,"recipient-kem","recipient","recovery-1",2,"author");
        rejected(()->transition(OWNER,"recipient-kem",publicJwk("recovery-1"),2,transitionSignature(OWNER,"recipient-kem","recipient","recovery-1",2,"recovery-1")));
        rejected(()->transition(OTHER,"recipient-kem",publicJwk("recovery-1"),2,transitionSignature(OTHER,"recipient-kem","recipient","recovery-1",2,"author")));
        rejected(()->transition(OWNER,"lease",publicJwk("recovery-1"),2,valid)); rejected(()->transition(OWNER,"envelope-author",publicJwk("recovery-1"),2,valid));
        rejected(()->transition(OWNER,"recipient-kem",publicJwk("recovery-2"),2,valid));
        rejected(()->transition(OWNER,"recipient-kem",publicJwk("recovery-1"),1,transitionSignature(OWNER,"recipient-kem","recipient","recovery-1",1,"author")));
        assertArrayEquals(root,open(saved));
    }
    @Test void GivenSameKeyForTwoRoles_WhenTrustOrTransitionCreated_ThenReject() {
        rejected(()->trust(publicJwk("author"),publicJwk("author")));
        rejected(()->transition(OWNER,"recipient-kem",publicJwk("author"),2,transitionSignature(OWNER,"recipient-kem","recipient","author",2,"author")));
    }
    @Test void GivenMutableInputPins_WhenCallerChangesThem_ThenTrustedKeysUnchanged() {
        var recipient=new LinkedHashMap<>(publicJwk("recipient")); var author=new LinkedHashMap<>(publicJwk("author")); var pins=trust(recipient,author);
        recipient.put("x",ProtocolJson.base64(new byte[32])); author.put("x",ProtocolJson.base64(new byte[32])); assertArrayEquals(root,open(wrap(),resource,GRANT,1,IDENTITY,privateDer("recipient"),pins));
    }
    @Test void GivenNewGrantRevision_WhenRegranted_ThenStaleRejectedButOldCopiedMaterialRemains() {
        var old=wrap(); var fresh=wrap(root,resource,GRANT,2,IDENTITY,publicJwk("recipient"),privateDer("author"));
        rejected(()->open(old,resource,GRANT,2,IDENTITY,privateDer("recipient"),trust)); assertArrayEquals(root,open(fresh,resource,GRANT,2,IDENTITY,privateDer("recipient"),trust)); assertArrayEquals(root,open(old));
        var permitted=Map.of(IDENTITY,publicJwk("recipient"),OTHER,publicJwk("recovery-1")); Map<String,Object> wrapped=new HashMap<>();
        for(var recipient:permitted.entrySet()) if(!recipient.getKey().equals(OTHER)) wrapped.put(recipient.getKey(),wrap(root,resource,GRANT,2,recipient.getKey(),recipient.getValue(),privateDer("author")));
        assertFalse(wrapped.containsKey(OTHER));
    }
}
