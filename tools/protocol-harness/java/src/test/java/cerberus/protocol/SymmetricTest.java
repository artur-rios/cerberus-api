package cerberus.protocol;

import java.lang.reflect.InvocationTargetException;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;
import static cerberus.protocol.Fixtures.*;

class SymmetricTest {
    private static final String OWNER="00000000-0000-0000-0000-000000000001", RESOURCE="00000000-0000-0000-0000-000000000002", OTHER="00000000-0000-0000-0000-000000000003", FORMAT="cerberus-content-v1";
    private Class<?> contextClass,guardClass;
    private byte[] root,salt,nonce,plaintext;
    private Object expected,guard;
    @BeforeEach void setup() {
        contextClass=assertDoesNotThrow(()->Class.forName("cerberus.protocol.Context"),"Context feature missing");
        guardClass=assertDoesNotThrow(()->Class.forName("cerberus.protocol.NonceGuard"),"NonceGuard feature missing");
        root=new byte[32]; salt=new byte[32]; nonce=new byte[12];
        for(int i=0;i<32;i++) { root[i]=(byte)i; salt[i]=(byte)(32+i); }
        for(int i=0;i<12;i++) nonce[i]=(byte)i;
        plaintext="public fixture".getBytes(StandardCharsets.US_ASCII);
        expected=ctx(OWNER,"record",RESOURCE,1,List.of()); guard=guard(0);
    }
    private Object construct(Class<?> cls,Class<?>[] types,Object... args) {
        try { return cls.getConstructor(types).newInstance(args); }
        catch(InvocationTargetException ex) {
            if(ex.getCause() instanceof RuntimeException runtime) throw runtime;
            throw new AssertionError(ex.getCause());
        } catch(ReflectiveOperationException ex) { throw new AssertionError(ex); }
    }
    private Object ctx(String owner,String kind,String resource,long epoch,List<Object> extra) {
        return construct(contextClass,new Class<?>[]{String.class,String.class,String.class,long.class,List.class},owner,kind,resource,epoch,extra);
    }
    private Object guard(long count) { return construct(guardClass,new Class<?>[]{long.class},count); }
    private Map<String,Object> seal(byte[] key,Object context,String format,byte[] plain,Object state,byte[] s,byte[] n) {
        return map(call("Symmetric","sealFixture",new Class<?>[]{byte[].class,contextClass,String.class,byte[].class,guardClass,byte[].class,byte[].class},key,context,format,plain,state,s,n));
    }
    private Map<String,Object> seal() { return seal(root,expected,FORMAT,plaintext,guard,salt,nonce); }
    private byte[] open(byte[] key,Object context,String format,Map<String,Object> envelope) {
        return (byte[])call("Symmetric","open",new Class<?>[]{byte[].class,contextClass,String.class,Map.class},key,context,format,envelope);
    }
    @Test void GivenUsedSaltContext_WhenSealedAgain_ThenRejected() {
        Map<String,Object> envelope=seal(); assertArrayEquals(plaintext,open(root,expected,FORMAT,envelope));
        rejected(()->seal(root,expected,FORMAT,new byte[]{1},guard,salt,new byte[12]));
    }
    @Test void GivenExpectedContext_WhenFixtureSealed_ThenIndependentWorkedEnvelopeMatches() {
        assertEquals(Map.of("format",FORMAT,"keyEpoch",1L,"keySalt","ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8",
            "nonce","AAECAwQFBgcICQoL","ciphertext","xvJT8pCjlfo5xRIRpss","tag","S66V63rqlxPAHtHfeemDGg"),seal());
    }
    @Test void GivenAllContentKinds_WhenSealed_ThenExpectedContextOpens() {
        for(String kind:List.of("account","profile","record","folder","collection")) {
            Object context=ctx(OWNER,kind,RESOURCE,1,List.of());
            assertArrayEquals(plaintext,open(root,context,FORMAT,seal(root,context,FORMAT,plaintext,guard,salt,nonce)));
        }
    }
    @Test void GivenChangedTrustedContext_WhenOpened_ThenReject() {
        Map<String,Object> envelope=seal();
        for(Object changed:List.of(ctx(OTHER,"record",RESOURCE,1,List.of()),ctx(OWNER,"folder",RESOURCE,1,List.of()),
            ctx(OWNER,"record",OTHER,1,List.of()),ctx(OWNER,"record",RESOURCE,2,List.of()),ctx(OWNER,"record",RESOURCE,1,List.of("changed-purpose"))))
            rejected(()->open(root,changed,FORMAT,envelope));
        rejected(()->open(new byte[32],expected,FORMAT,envelope));
    }
    @Test void GivenChangedEnvelopeField_WhenOpened_ThenReject() {
        Map<String,Object> envelope=seal();
        for(Map.Entry<String,Object> change:Map.<String,Object>of("format","unknown-v1","keyEpoch",2L,"keySalt",ProtocolJson.base64(new byte[32]),
            "nonce",ProtocolJson.base64(new byte[12]),"ciphertext",ProtocolJson.base64(new byte[]{1}),"tag",ProtocolJson.base64(new byte[16])).entrySet()) {
            Map<String,Object> value=new LinkedHashMap<>(envelope); value.put(change.getKey(),change.getValue()); rejected(()->open(root,expected,FORMAT,value));
        }
        for(Object value:Arrays.asList("", "A", null)) {
            Map<String,Object> changed=new LinkedHashMap<>(envelope); changed.put("ciphertext",value); rejected(()->open(root,expected,FORMAT,changed));
        }
        Map<String,Object> changed=new LinkedHashMap<>(envelope); changed.put("keyEpoch",true); rejected(()->open(root,expected,FORMAT,changed));
    }
    @Test void GivenUnknownOrMissingFields_WhenOpened_ThenReject() {
        Map<String,Object> envelope=seal();
        Map<String,Object> missing=new LinkedHashMap<>(envelope); missing.remove("tag"); rejected(()->open(root,expected,FORMAT,missing));
        Map<String,Object> extra=new LinkedHashMap<>(envelope); extra.put("kdf",Map.of()); rejected(()->open(root,expected,FORMAT,extra));
        rejected(()->open(root,expected,FORMAT,null));
    }
    @Test void GivenWrapperFormatOrPurpose_WhenContentApiUsed_ThenReject() {
        rejected(()->seal(root,expected,"cerberus-password-wrap-v1",plaintext,guard,salt,nonce));
        rejected(()->seal(root,ctx(OWNER,"record",RESOURCE,1,List.of("wrong-purpose")),FORMAT,plaintext,guard,salt,nonce));
    }
    @Test void GivenNormalSeal_WhenCalledTwice_ThenFreshSaltAndNonce() {
        Class<?>[] types={byte[].class,contextClass,String.class,byte[].class,guardClass};
        Map<String,Object> one=map(call("Symmetric","seal",types,root,expected,FORMAT,plaintext,guard));
        Map<String,Object> two=map(call("Symmetric","seal",types,root,expected,FORMAT,plaintext,guard));
        assertNotEquals(one.get("keySalt"),two.get("keySalt")); assertNotEquals(one.get("nonce"),two.get("nonce"));
    }
    @Test void GivenFailedEncryption_WhenSaltReused_ThenConsumedReservationRejects() {
        rejected(()->seal(root,expected,FORMAT,new byte[0],guard,salt,nonce)); rejected(this::seal);
    }
    @Test void GivenUnsentEnvelope_WhenSaltReused_ThenConsumedReservationRejects() {
        Map<String,Object> saved=seal(),retry=new LinkedHashMap<>(saved);
        assertEquals(saved,retry); assertArrayEquals(plaintext,open(root,expected,FORMAT,retry)); rejected(this::seal);
    }
    @Test void GivenLastBudgetReservation_WhenNextAttempted_ThenReject() {
        Object state=guard((1L<<32)-1); seal(root,expected,FORMAT,plaintext,state,salt,nonce);
        rejected(()->seal(root,expected,FORMAT,plaintext,state,new byte[32],nonce));
        seal(new byte[32],expected,FORMAT,plaintext,state,salt,nonce); seal(new byte[32],expected,FORMAT,plaintext,state,new byte[32],nonce);
        Object rotated=ctx(OWNER,"record",RESOURCE,2,List.of());
        seal(root,rotated,FORMAT,plaintext,state,salt,nonce); seal(root,rotated,FORMAT,plaintext,state,new byte[32],nonce);
    }
    @Test void GivenInvalidOrExhaustedBudget_WhenUsed_ThenReject() {
        rejected(()->guard(-1)); rejected(()->guard((1L<<32)+1));
        rejected(()->seal(root,expected,FORMAT,plaintext,guard(1L<<32),salt,nonce));
    }
    @Test void GivenConcurrentSaltReservations_WhenSealed_ThenExactlyOneWins() throws Exception {
        CyclicBarrier barrier=new CyclicBarrier(2);
        try(var pool=Executors.newFixedThreadPool(2)) {
            Callable<String> attempt=()-> { barrier.await(); try { seal(); return "accepted"; } catch(ProtocolError ex) { return "rejected"; } };
            var a=pool.submit(attempt); var b=pool.submit(attempt);
            assertEquals(Set.of("accepted","rejected"),Set.of(a.get(5,TimeUnit.SECONDS),b.get(5,TimeUnit.SECONDS)));
        }
    }
    @Test void GivenMutableExtraList_WhenChangedAfterConstruction_ThenContextIdentityIsUnchanged() {
        List<Object> extra=new ArrayList<>(); Object context=ctx(OWNER,"record",RESOURCE,1,extra); extra.add("changed");
        Map<String,Object> envelope=seal(root,context,FORMAT,plaintext,guard,salt,nonce);
        assertArrayEquals(plaintext,open(root,expected,FORMAT,envelope)); rejected(()->seal(root,context,FORMAT,plaintext,guard,salt,nonce));
    }
    @Test void GivenInvalidContext_WhenConstructed_ThenReject() {
        rejected(()->ctx(null,"record",RESOURCE,1,List.of())); rejected(()->ctx(OWNER,"é",RESOURCE,1,List.of()));
        rejected(()->ctx(OWNER,"record","invalid",1,List.of())); rejected(()->ctx(OWNER,"record",RESOURCE,0,List.of()));
        rejected(()->ctx(OWNER,"record",RESOURCE,1,null)); rejected(()->ctx(OWNER,"record",RESOURCE,1,List.of(Map.of())));
    }
    @Test void GivenNestedMutableContext_WhenChangedAfterReservation_ThenDuplicateStillRejects() throws Exception {
        List<Object> nested=new ArrayList<>(); List<Object> mutable=new ArrayList<>(); nested.add(1L); nested.add(null); nested.add(mutable);
        Object context=ctx(OWNER,"record",RESOURCE,1,List.of("purpose",nested));
        var reserve=guardClass.getMethod("reserve",byte[].class,contextClass,byte[].class);
        reserve.invoke(guard,root,context,salt); mutable.add("changed");
        InvocationTargetException error=assertThrows(InvocationTargetException.class,()->reserve.invoke(guard,root,context,salt));
        assertEquals("invalid_protocol",error.getCause().getMessage());
    }
    @Test void GivenAggregateLimitOrInvalidMaterial_WhenUsed_ThenReject() {
        rejected(()->seal(null,expected,FORMAT,plaintext,guard,salt,nonce));
        rejected(()->seal(new byte[31],expected,FORMAT,plaintext,guard,salt,nonce));
        rejected(()->seal(root,expected,FORMAT,plaintext,guard,new byte[31],nonce));
        rejected(()->seal(root,expected,FORMAT,plaintext,guard,salt,new byte[11]));
        rejected(()->seal(root,expected,FORMAT,null,guard(0),salt,nonce));
        rejected(()->seal(root,expected,FORMAT,new byte[1048576],guard(0),salt,nonce));
        rejected(()->seal(root,expected,FORMAT,plaintext,null,salt,nonce));
        Map<String,Object> envelope=seal(root,expected,FORMAT,plaintext,guard(0),salt,nonce);
        Map<String,Object> oversized=new LinkedHashMap<>(envelope); oversized.put("ciphertext","A".repeat(1048576));
        rejected(()->open(root,expected,FORMAT,oversized));
    }
}
