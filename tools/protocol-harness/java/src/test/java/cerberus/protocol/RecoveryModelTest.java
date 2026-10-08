package cerberus.protocol;

import java.lang.reflect.*;
import java.math.BigInteger;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicLong;
import java.util.function.LongSupplier;
import org.bouncycastle.asn1.sec.SECNamedCurves;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;
import static cerberus.protocol.Fixtures.*;

class RecoveryModelTest {
    static final String ACCOUNT="00000000-0000-0000-0000-000000000001",IDENTITY="00000000-0000-0000-0000-000000000002",OTHER="00000000-0000-0000-0000-000000000008";
    static final long NOW=1700000000;
    AtomicLong now=new AtomicLong(NOW);
    Object model;
    @BeforeEach void setup() { model=create(initial()); }
    Object create(Map<String,Object> state) {
        return assertDoesNotThrow(()->Class.forName("cerberus.protocol.RecoveryModel").getConstructor(Map.class,LongSupplier.class).newInstance(state,(LongSupplier)now::get),"RecoveryModel missing");
    }
    Object invoke(Object instance,String method,Class<?>[] types,Object... args) {
        try { return instance.getClass().getMethod(method,types).invoke(instance,args); }
        catch(InvocationTargetException ex) { if(ex.getCause() instanceof RuntimeException r) throw r; throw new AssertionError(ex.getCause()); }
        catch(ReflectiveOperationException ex) { throw new AssertionError(ex); }
    }
    Map<String,Object> snapshot(Object m) { return map(invoke(m,"snapshot",new Class<?>[]{})); }
    Map<String,Object> snapshot() { return snapshot(model); }
    static Map<String,Object> password(long epoch) {
        return Map.of("format","cerberus-password-wrap-v1","keyEpoch",epoch,"keySalt",ProtocolJson.base64(new byte[32]),"nonce",ProtocolJson.base64(new byte[12]),"ciphertext","AA","tag",ProtocolJson.base64(new byte[16]),
            "kdf",Map.of("algorithm","argon2id-v1.3","memoryKiB",65536L,"iterations",3L,"parallelism",4L,"salt",ProtocolJson.base64(new byte[16])));
    }
    static Map<String,Object> recovery(long epoch,long generation,Map<String,Object> verifier) {
        return Map.of("format","cerberus-recovery-wrap-v1","keyEpoch",epoch,"keySalt",ProtocolJson.base64(new byte[32]),"nonce",ProtocolJson.base64(new byte[12]),"ciphertext","AA","tag",ProtocolJson.base64(new byte[16]),"generation",generation,"proofKeyFingerprint",Keys.thumbprint(verifier));
    }
    static Map<String,Object> initial() {
        var s=new LinkedHashMap<String,Object>(); s.put("accountId",ACCOUNT); s.put("identityId",IDENTITY); s.put("scopeKind","account"); s.put("scopeId",ACCOUNT);
        for(String key:List.of("keyEpoch","protectionRevision","generation","revocationGeneration")) s.put(key,1L);
        s.put("unlockVerifier",publicJwk("unlock-account")); s.put("recoveryVerifier",publicJwk("recovery-1")); s.put("passwordWrapper",password(1)); s.put("recoveryWrapper",recovery(1,1,publicJwk("recovery-1"))); return s;
    }
    record Request(String identity,boolean auth,byte[] body,String challenge,byte[] proof,boolean access,boolean failure) {
        Request auth(boolean a) { return new Request(identity,a,body,challenge,proof,access,failure); }
        Request body(byte[] b) { return new Request(identity,auth,b,challenge,proof,access,failure); }
        Request proof(byte[] p) { return new Request(identity,auth,body,challenge,p,access,failure); }
        Request failure(boolean f) { return new Request(identity,auth,body,challenge,proof,access,f); }
    }
    Request request(Object m,String operation,String key,String role,Map<String,Object> changes,boolean register) {
        var state=map(snapshot(m).get("state")); long epoch=(Long)state.get("keyEpoch"),generation=(Long)state.get("generation");
        var body=new LinkedHashMap<String,Object>(); body.put("operation",operation); body.put("idempotencyKey",key); body.put("expectedRevision",state.get("protectionRevision"));
        var newKey=generation==1?publicJwk("recovery-2"):Keys.publicJwk(Keys.generate());
        body.put("passwordWrapper",password(epoch+1)); body.put("recoveryWrapper",recovery(epoch+1,generation+1,newKey)); body.put("newRecoveryVerifier",newKey); body.putAll(changes);
        byte[] raw=Wire.encode(body); var binding=new LinkedHashMap<String,Object>();
        for(String field:List.of("identityId","accountId","scopeKind","scopeId","keyEpoch","protectionRevision")) binding.put(field,state.get(field));
        binding.put("operation",operation); binding.put("generation",operation.equals("recover")?generation:null);
        var issued=Challenge.issue(binding,raw,NOW); if(register) invoke(m,"addChallenge",new Class<?>[]{Map.class},issued);
        return new Request(IDENTITY,true,raw,(String)issued.get("challengeId"),Challenge.sign(issued,privateDer(role)),true,false);
    }
    Request request(String key) { return request(model,"recover",key,"recovery-1",Map.of(),true); }
    Map<String,Object> execute(Object m,Request r) {
        return map(invoke(m,"execute",new Class<?>[]{String.class,boolean.class,byte[].class,String.class,byte[].class,boolean.class,boolean.class},r.identity,r.auth,r.body,r.challenge,r.proof,r.access,r.failure));
    }
    Map<String,Object> execute(Request r) { return execute(model,r); }
    void rejectUnchanged(Request r) { var before=snapshot(); rejected(()->execute(r)); assertEquals(before,snapshot()); }
    @Test void GivenConcurrentRecovery_WhenCommitted_ThenOneTransitionWins() throws Exception {
        try(var pool=Executors.newFixedThreadPool(2)) {
            for(int iteration=0;iteration<100;iteration++) {
                Object m=create(initial()); var first=request(m,"recover","first","recovery-1",Map.of(),true); var second=request(m,"recover","second","recovery-1",Map.of(),true);
                var before=snapshot(m); var barrier=new CyclicBarrier(2); List<Future<Map<String,Object>>> futures=new ArrayList<>();
                for(var r:List.of(first,second)) futures.add(pool.submit(()->{ barrier.await(10,TimeUnit.SECONDS); try { return execute(m,r); } catch(ProtocolError ex) { return Map.of("status","rejected"); } }));
                var outcomes=List.of(futures.get(0).get(10,TimeUnit.SECONDS),futures.get(1).get(10,TimeUnit.SECONDS));
                assertEquals(1,outcomes.stream().filter(x->x.get("status").equals("committed")).count()); var after=snapshot(m); var state=map(after.get("state"));
                assertEquals(2L,state.get("protectionRevision")); assertEquals(2L,state.get("revocationGeneration")); assertEquals(2L,state.get("generation"));
                assertEquals(1,((List<?>)after.get("consumed")).size()); assertEquals(1,((List<?>)after.get("results")).size());
                int winner=outcomes.get(0).get("status").equals("committed")?0:1; assertEquals(outcomes.get(winner),execute(m,winner==0?first:second)); assertEquals(after,snapshot(m)); assertNotEquals(before,after);
            }
        }
    }
    @Test void GivenExactRetry_WhenSignatureMalleatedOrExpired_ThenStoredResultReturned() {
        var r=request("one"); var outcome=execute(r); var after=snapshot(); byte[] p=r.proof.clone();
        BigInteger n=SECNamedCurves.getByName("secp256r1").getN(),s=new BigInteger(1,Arrays.copyOfRange(p,32,64));
        byte[] alternate=n.subtract(s).toByteArray(); Arrays.fill(p,32,64,(byte)0); int len=Math.min(32,alternate.length); System.arraycopy(alternate,alternate.length-len,p,64-len,len);
        now.set(NOW+60); assertEquals(outcome,execute(r.proof(p))); assertEquals(after,snapshot()); assertEquals(Set.of("status","protectionRevision","generation","revocationGeneration"),outcome.keySet());
    }
    @Test void GivenStoredResult_WhenIdentityBodyKeyOrAuthChanges_ThenRejected() {
        var r=request("one"); execute(r); rejectUnchanged(r.auth(false));
        rejectUnchanged(new Request(OTHER,true,r.body,r.challenge,r.proof,true,false));
        rejectUnchanged(r.body((new String(r.body,java.nio.charset.StandardCharsets.UTF_8)+" ").getBytes(java.nio.charset.StandardCharsets.UTF_8)));
        rejectUnchanged(r.body(new String(r.body,java.nio.charset.StandardCharsets.UTF_8).replace("\"one\"","\"two\"").getBytes(java.nio.charset.StandardCharsets.UTF_8)));
    }
    @Test void GivenUnknownExpiredOrWrongPurposeChallenge_WhenExecuted_ThenRejected() {
        rejectUnchanged(request(model,"recover","unknown","recovery-1",Map.of(),false)); var r=request("expired"); now.set(NOW+60); rejectUnchanged(r); now.set(NOW);
        rejectUnchanged(request(model,"recover","replacement","recovery-2",Map.of(),true)); rejectUnchanged(request(model,"recover","unlock","unlock-account",Map.of(),true));
    }
    @Test void GivenStaleRevisionOrGeneration_WhenExecuted_ThenRejected() {
        rejectUnchanged(request(model,"recover","bad","recovery-1",Map.of("expectedRevision",2L),true)); var stale=request("stale"); execute(request("winner")); rejectUnchanged(stale);
    }
    @Test void GivenRefreshWithoutCurrentAccess_WhenExecuted_ThenRejected() {
        var r=request(model,"refresh-recovery","refresh","unlock-account",Map.of(),true); rejectUnchanged(new Request(r.identity,true,r.body,r.challenge,r.proof,false,false));
    }
    @Test void GivenRefresh_WhenCommitted_ThenOldGenerationInvalidated() {
        var old=request("old"); var refresh=request(model,"refresh-recovery","refresh","unlock-account",Map.of(),true); assertEquals(2L,execute(refresh).get("generation")); rejectUnchanged(old);
        assertEquals(3L,execute(request(model,"recover","new","recovery-2",Map.of(),true)).get("generation"));
    }
    @Test void GivenInjectedFailure_WhenExecuted_ThenEveryEffectRolledBack() { var r=request("one"); rejectUnchanged(r.failure(true)); assertEquals("committed",execute(r).get("status")); }
    @Test void GivenMalformedReplacementMetadata_WhenExecuted_ThenRejected() {
        var valid=request("one"); var body=ProtocolJson.object(valid.body,1048576);
        for(var change:Map.<String,Object>of("expectedRevision",true,"idempotencyKey","","operation","unlock-account","newRecoveryVerifier",publicJwk("recovery-1"),"passwordWrapper",Map.of(),"recoveryWrapper",Map.of(),"extra",1L).entrySet()) {
            rejectUnchanged(request(model,"recover","bad-"+change.getKey(),"recovery-1",Map.of(change.getKey(),change.getValue()),true));
        }
    }
    @Test void GivenDuplicateChallengeOrMutableSnapshot_WhenUsed_ThenRegistryProtected() {
        var r=request("one"); var s=snapshot(); var issued=map(map(s.get("challenges")).values().iterator().next()); rejected(()->invoke(model,"addChallenge",new Class<?>[]{Map.class},issued));
        map(map(s.get("state")).get("recoveryVerifier")).put("x","AA"); assertNotEquals(s,snapshot()); assertEquals("committed",execute(r).get("status"));
    }
    @Test void GivenRevisionOverflow_WhenExecuted_ThenNoPartialChanges() { var state=initial(); state.put("revocationGeneration",ProtocolJson.MAX_INTEGER); model=create(state); rejectUnchanged(request("one")); }
}
