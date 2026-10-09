package cerberus.protocol;

import java.util.*;
import java.util.function.LongSupplier;

/** Locked local reference model only, not PostgreSQL/Heimdall permission verification. */
public final class RecoveryModel {
    private static final Set<String> STATE_FIELDS=Set.of("accountId","identityId","scopeKind","scopeId","keyEpoch","protectionRevision","generation","revocationGeneration","unlockVerifier","recoveryVerifier","passwordWrapper","recoveryWrapper");
    private static final Set<String> BODY_FIELDS=Set.of("operation","idempotencyKey","expectedRevision","passwordWrapper","recoveryWrapper","newRecoveryVerifier");
    private Map<String,Object> state;
    private final LongSupplier clock;
    private final Map<String,Map<String,Object>> challenges=new TreeMap<>();
    private final Set<String> consumed=new TreeSet<>();
    private record Identity(String identity,String key) {}
    private record Result(String digest,Map<String,Object> outcome) {}
    private final Map<Identity,Result> results=new LinkedHashMap<>();
    private static Map<String,Object> map(Object value) { if(!(value instanceof Map<?,?>)) throw new ProtocolError(); return ProtocolJson.fields(value,((Map<?,?>)value).keySet().stream().map(x->ProtocolJson.text(x,true)).collect(java.util.stream.Collectors.toSet()),Set.of()); }
    private static Map<String,Object> copy(Map<String,Object> value) { return ProtocolJson.object(Wire.encode(value),ProtocolJson.MAX_REQUEST); }
    private static long number(Object value) { return ProtocolJson.integer(value,1,ProtocolJson.MAX_INTEGER); }
    private static void wrappers(Map<String,Object> password,Map<String,Object> recovery,String account,long epoch,long generation,Map<String,Object> verifier) {
        ProtocolJson.fields(password,Protection.WRAPPER_FIELDS,Set.of());
        var kdf=ProtocolJson.fields(password.get("kdf"),Set.of("algorithm","memoryKiB","iterations","parallelism","salt"),Set.of());
        if(!"argon2id-v1.3".equals(kdf.get("algorithm")) || number(kdf.get("memoryKiB"))!=65536 || number(kdf.get("iterations"))!=3 || number(kdf.get("parallelism"))!=4) throw new ProtocolError();
        ProtocolJson.unbase64(ProtocolJson.text(kdf.get("salt"),true),16);
        var slot=new Context(account,"account-protection",account,epoch,List.of("argon2id-v1.3",65536L,3L,4L,kdf.get("salt")));
        Symmetric.validateBound(slot,Protection.FORMAT,Protection.envelope(password));
        ProtocolJson.fields(recovery,RecoveryBundle.WRAPPER_FIELDS,Set.of()); String fingerprint=Keys.thumbprint(verifier);
        if(number(recovery.get("generation"))!=generation || !fingerprint.equals(recovery.get("proofKeyFingerprint"))) throw new ProtocolError();
        Symmetric.validateBound(new Context(account,"recovery",account,epoch,List.of(generation,fingerprint)),RecoveryBundle.FORMAT,Protection.envelope(recovery));
    }
    public RecoveryModel(Map<String,Object> initialPublicState,LongSupplier clock) {
        state=Protection.snapshot(initialPublicState,STATE_FIELDS);
        for(String field:List.of("accountId","identityId","scopeId")) ProtocolJson.guid(ProtocolJson.text(state.get(field),true));
        if(clock==null || !"account".equals(state.get("scopeKind")) || !state.get("accountId").equals(state.get("scopeId"))) throw new ProtocolError();
        this.clock=clock;
        for(String field:List.of("keyEpoch","protectionRevision","generation","revocationGeneration")) number(state.get(field));
        var unlock=Keys.validatePublic(map(state.get("unlockVerifier"))); var recovery=Keys.validatePublic(map(state.get("recoveryVerifier")));
        if(Keys.thumbprint(unlock).equals(Keys.thumbprint(recovery))) throw new ProtocolError();
        wrappers(map(state.get("passwordWrapper")),map(state.get("recoveryWrapper")),(String)state.get("accountId"),number(state.get("keyEpoch")),number(state.get("generation")),recovery);
    }
    public synchronized void addChallenge(Map<String,Object> issued) {
        var value=Challenge.validate(issued); String id=(String)value.get("challengeId");
        if(challenges.containsKey(id)) throw new ProtocolError(); challenges.put(id,value);
    }
    public synchronized Map<String,Object> snapshot() {
        List<Object> stored=new ArrayList<>();
        results.entrySet().stream().sorted(Comparator.comparing((Map.Entry<Identity,Result> e)->e.getKey().identity).thenComparing(e->e.getKey().key)).forEach(e->stored.add(Map.of("identityId",e.getKey().identity,"idempotencyKey",e.getKey().key,"requestHash",e.getValue().digest,"outcome",e.getValue().outcome)));
        return copy(Map.of("state",state,"challenges",challenges,"consumed",new ArrayList<>(consumed),"results",stored));
    }
    public synchronized Map<String,Object> execute(String identityId,boolean freshAuth,byte[] rawBody,String challengeId,byte[] proof,boolean currentVaultAccess,boolean injectCommitFailure) {
        ProtocolJson.guid(identityId);
        if(!freshAuth || !identityId.equals(state.get("identityId")) || rawBody==null) throw new ProtocolError();
        byte[] raw=rawBody.clone(); var body=ProtocolJson.parse(raw,BODY_FIELDS,Set.of(),ProtocolJson.MAX_REQUEST);
        String key=ProtocolJson.text(body.get("idempotencyKey"),true); if(key.isEmpty()) throw new ProtocolError();
        String digest=ProtocolJson.base64(Primitives.sha256(raw)); var identity=new Identity(identityId,key); Result prior=results.get(identity);
        if(prior!=null) { if(!digest.equals(prior.digest)) throw new ProtocolError(); return copy(prior.outcome); }
        String operation=ProtocolJson.text(body.get("operation"),true);
        if(!Set.of("recover","refresh-recovery").contains(operation) || operation.equals("refresh-recovery") && !currentVaultAccess) throw new ProtocolError();
        ProtocolJson.guid(challengeId); if(!challenges.containsKey(challengeId) || consumed.contains(challengeId)) throw new ProtocolError();
        if(number(body.get("expectedRevision"))!=number(state.get("protectionRevision"))) throw new ProtocolError();
        var binding=new LinkedHashMap<String,Object>(); for(String field:List.of("identityId","accountId","scopeKind","scopeId","keyEpoch","protectionRevision")) binding.put(field,state.get(field));
        binding.put("operation",operation); binding.put("generation",operation.equals("recover")?state.get("generation"):null);
        var verifier=map(state.get(operation.equals("recover")?"recoveryVerifier":"unlockVerifier"));
        Challenge.verify(challenges.get(challengeId),binding,raw,verifier,proof,clock.getAsLong());
        var next=copy(state); for(String field:List.of("keyEpoch","protectionRevision","generation","revocationGeneration")) next.put(field,number(number(state.get(field))+1));
        var replacement=Keys.validatePublic(map(body.get("newRecoveryVerifier"))); String fp=Keys.thumbprint(replacement);
        if(fp.equals(Keys.thumbprint(map(state.get("recoveryVerifier")))) || fp.equals(Keys.thumbprint(map(state.get("unlockVerifier"))))) throw new ProtocolError();
        var password=map(body.get("passwordWrapper")); var recovery=map(body.get("recoveryWrapper"));
        wrappers(password,recovery,(String)state.get("accountId"),number(next.get("keyEpoch")),number(next.get("generation")),replacement);
        next.put("passwordWrapper",password); next.put("recoveryWrapper",recovery); next.put("recoveryVerifier",replacement);
        Map<String,Object> outcome=Map.of("status","committed","protectionRevision",next.get("protectionRevision"),"generation",next.get("generation"),"revocationGeneration",next.get("revocationGeneration"));
        if(injectCommitFailure) throw new ProtocolError();
        state=next; consumed.add(challengeId); results.put(identity,new Result(digest,outcome)); return copy(outcome);
    }
}
