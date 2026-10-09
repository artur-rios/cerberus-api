package cerberus.protocol;

import java.util.*;

/** Stateless proofs only; registry membership and atomic consumption are separate. */
public final class Challenge {
    private Challenge() {}
    static final String FORMAT="cerberus-challenge-v1";
    static final Set<String> BINDING_FIELDS=Set.of("operation","identityId","accountId","scopeKind","scopeId","keyEpoch","protectionRevision","generation");
    static final Set<String> FIELDS=Set.of("format","challengeId","nonce","operation","identityId","accountId","scopeKind","scopeId","keyEpoch","protectionRevision","generation","requestHash","issuedAt","expiresAt");
    private static final Set<String> OPERATIONS=Set.of("unlock-account","unlock-profile","change-protection","recover","refresh-recovery");
    static Map<String,Object> binding(Map<String,Object> expected) {
        Map<String,Object> value=Protection.snapshot(expected,BINDING_FIELDS);
        String operation=ProtocolJson.text(value.get("operation"),true),scope=ProtocolJson.text(value.get("scopeKind"),true);
        if(!OPERATIONS.contains(operation) || !Set.of("account","profile").contains(scope)) throw new ProtocolError();
        for(String field:List.of("identityId","accountId","scopeId")) ProtocolJson.guid(ProtocolJson.text(value.get(field),true));
        ProtocolJson.integer(value.get("keyEpoch"),1,ProtocolJson.MAX_INTEGER); ProtocolJson.integer(value.get("protectionRevision"),1,ProtocolJson.MAX_INTEGER);
        if(scope.equals("account") && !value.get("scopeId").equals(value.get("accountId"))) throw new ProtocolError();
        if(operation.equals("unlock-account") && !scope.equals("account") || operation.equals("unlock-profile") && !scope.equals("profile")) throw new ProtocolError();
        if(operation.equals("recover")) {
            if(!scope.equals("account")) throw new ProtocolError(); ProtocolJson.integer(value.get("generation"),1,ProtocolJson.MAX_INTEGER);
        } else if(value.get("generation")!=null) throw new ProtocolError();
        return value;
    }
    private static String bodyHash(byte[] raw) {
        if(raw==null || raw.length>ProtocolJson.MAX_REQUEST) throw new ProtocolError();
        byte[] snapshot=raw.clone(),digest=Primitives.sha256(snapshot); ProtocolJson.object(snapshot,ProtocolJson.MAX_REQUEST); return ProtocolJson.base64(digest);
    }
    static Map<String,Object> validate(Map<String,Object> challenge) {
        Map<String,Object> value=Protection.snapshot(challenge,FIELDS),binding=new LinkedHashMap<>();
        for(String field:BINDING_FIELDS) binding.put(field,value.get(field)); binding(binding);
        if(!FORMAT.equals(value.get("format"))) throw new ProtocolError();
        ProtocolJson.guid(ProtocolJson.text(value.get("challengeId"),true)); ProtocolJson.unbase64(ProtocolJson.text(value.get("nonce"),true),32); ProtocolJson.unbase64(ProtocolJson.text(value.get("requestHash"),true),32);
        long issued=ProtocolJson.integer(value.get("issuedAt"),0,ProtocolJson.MAX_INTEGER-60);
        if(ProtocolJson.integer(value.get("expiresAt"),0,ProtocolJson.MAX_INTEGER)!=issued+60) throw new ProtocolError();
        return value;
    }
    public static Map<String,Object> issue(Map<String,Object> expectedBinding,byte[] rawBody,long now) {
        return issueFixture(expectedBinding,rawBody,now,UUID.randomUUID().toString(),Protection.random(32));
    }
    public static Map<String,Object> issueFixture(Map<String,Object> expectedBinding,byte[] rawBody,long now,String challengeId,byte[] nonce) {
        Map<String,Object> value=binding(expectedBinding); ProtocolJson.integer(now,0,ProtocolJson.MAX_INTEGER-60); ProtocolJson.guid(challengeId);
        if(nonce==null || nonce.length!=32) throw new ProtocolError();
        value.put("format",FORMAT); value.put("challengeId",challengeId); value.put("nonce",ProtocolJson.base64(nonce)); value.put("requestHash",bodyHash(rawBody)); value.put("issuedAt",now); value.put("expiresAt",now+60); return validate(value);
    }
    public static byte[] bytes(Map<String,Object> challenge) {
        Map<String,Object> value=validate(challenge); List<Object> array=new ArrayList<>(); array.add("cerberus-proof-v1");
        for(String field:List.of("challengeId","nonce","operation","identityId","accountId","scopeKind","scopeId","keyEpoch","protectionRevision","generation","requestHash","issuedAt","expiresAt")) array.add(value.get(field));
        return ProtocolJson.context(array);
    }
    public static byte[] sign(Map<String,Object> challenge,byte[] scopedPrivateDer) { return Keys.sign(scopedPrivateDer,bytes(challenge)); }
    public static void verify(Map<String,Object> challenge,Map<String,Object> expectedBinding,byte[] rawBody,Map<String,Object> registeredJwk,byte[] signature,long now) {
        Map<String,Object> value=validate(challenge),expected=binding(expectedBinding); ProtocolJson.integer(now,0,ProtocolJson.MAX_INTEGER);
        if(now<(Long)value.get("issuedAt") || now>=(Long)value.get("expiresAt")) throw new ProtocolError();
        for(String field:BINDING_FIELDS) if(!Objects.equals(value.get(field),expected.get(field))) throw new ProtocolError();
        if(!value.get("requestHash").equals(bodyHash(rawBody)) || !Keys.verify(registeredJwk,bytes(value),signature)) throw new ProtocolError();
    }
}
