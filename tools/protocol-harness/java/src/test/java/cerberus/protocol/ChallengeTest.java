package cerberus.protocol;

import java.util.*;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;
import static cerberus.protocol.Fixtures.*;

class ChallengeTest {
    static final String OWNER="00000000-0000-0000-0000-000000000001",PROFILE="00000000-0000-0000-0000-000000000002",IDENTITY="00000000-0000-0000-0000-000000000003",ID="00000000-0000-0000-0000-000000000004";
    static final long NOW=1700000000L;
    byte[] body=ProtocolJson.utf8("{\"idempotencyKey\":\"fixture\",\"expectedRevision\":1}"),reordered=ProtocolJson.utf8("{\"expectedRevision\":1,\"idempotencyKey\":\"fixture\"}"),nonce=new byte[32];
    Map<String,Object> binding;
    @BeforeEach void setup() {
        assertDoesNotThrow(()->Class.forName("cerberus.protocol.Challenge"),"Challenge feature missing");
        binding=new LinkedHashMap<>(Map.of("operation","unlock-account","identityId",IDENTITY,"accountId",OWNER,"scopeKind","account","scopeId",OWNER,"keyEpoch",1L,"protectionRevision",1L)); binding.put("generation",null);
        for(int i=0;i<32;i++) nonce[i]=(byte)i;
    }
    Map<String,Object> issue(Map<String,Object> expected,byte[] raw,long now) { return map(call("Challenge","issueFixture",new Class<?>[]{Map.class,byte[].class,long.class,String.class,byte[].class},expected,raw,now,ID,nonce)); }
    Map<String,Object> issue() { return issue(binding,body,NOW); }
    byte[] bytes(Map<String,Object> value) { return (byte[])call("Challenge","bytes",new Class<?>[]{Map.class},value); }
    byte[] sign(Map<String,Object> value,String role) { return (byte[])call("Challenge","sign",new Class<?>[]{Map.class,byte[].class},value,privateDer(role)); }
    void verify(Map<String,Object> value,Map<String,Object> expected,byte[] raw,String role,byte[] proof,long now) {
        call("Challenge","verify",new Class<?>[]{Map.class,Map.class,byte[].class,Map.class,byte[].class,long.class},value,expected,raw,publicJwk(role),proof,now);
    }
    void verify(Map<String,Object> value) { verify(value,binding,body,"unlock-account",sign(value,"unlock-account"),NOW); }
    @Test void GivenBoundBody_WhenByteOrderChanges_ThenProofRejected() {
        var value=issue(); byte[] proof=sign(value,"unlock-account"); assertEquals(NOW+60,value.get("expiresAt")); assertEquals(ProtocolJson.base64(Primitives.sha256(body)),value.get("requestHash"));
        verify(value,binding,body,"unlock-account",proof,NOW+59);
        rejected(()->verify(value,binding,body,"unlock-account",proof,NOW+60)); rejected(()->verify(value,binding,reordered,"unlock-account",proof,NOW));
        assertNotEquals(value.get("requestHash"),issue(binding,reordered,NOW).get("requestHash"));
    }
    @Test void GivenChallenge_WhenEncoded_ThenExactLiteralArrayAndDeterministicSignature() {
        var value=issue(); List<Object> literal=new ArrayList<>(List.of("cerberus-proof-v1",ID,ProtocolJson.base64(nonce),"unlock-account",IDENTITY,OWNER,"account",OWNER,1L,1L));
        literal.add(null); literal.add(ProtocolJson.base64(Primitives.sha256(body))); literal.add(NOW); literal.add(NOW+60);
        assertArrayEquals(ProtocolJson.context(literal),bytes(value)); assertArrayEquals(sign(value,"unlock-account"),sign(value,"unlock-account"));
        List<String> keys=new ArrayList<>(value.keySet()); Collections.reverse(keys); Map<String,Object> ordered=new LinkedHashMap<>(); for(String key:keys) ordered.put(key,value.get(key)); assertArrayEquals(bytes(value),bytes(ordered));
    }
    @Test void GivenEveryChangedChallengeField_WhenVerified_ThenRejected() {
        var original=issue(); byte[] proof=sign(original,"unlock-account");
        Map<String,Object> changes=new LinkedHashMap<>(); changes.put("format","other"); changes.put("challengeId",PROFILE); changes.put("nonce",ProtocolJson.base64(new byte[32])); changes.put("operation","change-protection"); changes.put("identityId",PROFILE);
        changes.put("accountId",PROFILE); changes.put("scopeKind","profile"); changes.put("scopeId",PROFILE); changes.put("keyEpoch",2L); changes.put("protectionRevision",2L); changes.put("generation",1L); changes.put("requestHash",ProtocolJson.base64(new byte[32])); changes.put("issuedAt",NOW-1); changes.put("expiresAt",NOW+59);
        for(var change:changes.entrySet()) { var value=new LinkedHashMap<>(original); value.put(change.getKey(),change.getValue()); rejected(()->verify(value,binding,body,"unlock-account",proof,NOW)); }
    }
    @Test void GivenChangedExpectedBinding_WhenVerified_ThenRejected() {
        var value=issue(); byte[] proof=sign(value,"unlock-account");
        for(var change:Map.<String,Object>of("operation","change-protection","identityId",PROFILE,"accountId",PROFILE,"scopeKind","profile","scopeId",PROFILE,"keyEpoch",2L,"protectionRevision",2L,"generation",1L).entrySet()) {
            var expected=new LinkedHashMap<>(binding); expected.put(change.getKey(),change.getValue()); rejected(()->verify(value,expected,body,"unlock-account",proof,NOW));
        }
    }
    @Test void GivenAllOperationScopes_WhenIssued_ThenCorrectPurposeKeyRequired() {
        for(String operation:List.of("unlock-account","unlock-profile","change-protection","recover","refresh-recovery")) {
            var expected=new LinkedHashMap<>(binding); expected.put("operation",operation); String role="unlock-account";
            if(operation.equals("unlock-profile")) { expected.put("scopeKind","profile"); expected.put("scopeId",PROFILE); role="unlock-profile"; }
            if(operation.equals("recover")) { expected.put("generation",1L); role="recovery-1"; }
            var value=issue(expected,body,NOW); byte[] proof=sign(value,role); verify(value,expected,body,role,proof,NOW);
            rejected(()->verify(value,expected,body,"lease",proof,NOW));
        }
    }
    @Test void GivenInvalidScopeOperationOrGeneration_WhenIssued_ThenRejected() {
        List<Map<String,Object>> changes=List.of(Map.of("scopeId",PROFILE),Map.of("operation","unknown"),Map.of("generation",1L),Map.of("generation",true),Map.of("keyEpoch",true),Map.of("protectionRevision",0L),Map.of("identityId","bad"),Map.of("extra",1L),Map.of("operation","recover","generation",0L),Map.of("operation","recover","generation",1L,"scopeKind","profile","scopeId",PROFILE),Map.of("operation","unlock-profile"),Map.of("operation","unlock-account","scopeKind","profile","scopeId",PROFILE));
        for(var change:changes) { var value=new LinkedHashMap<>(binding); value.putAll(change); rejected(()->issue(value,body,NOW)); }
        var nullGeneration=new LinkedHashMap<>(binding); nullGeneration.put("operation","recover"); rejected(()->issue(nullGeneration,body,NOW));
        var missing=new LinkedHashMap<>(binding); missing.remove("generation"); rejected(()->issue(missing,body,NOW));
    }
    @Test void GivenFutureExpiredOrOverflowTime_WhenUsed_ThenRejected() {
        var value=issue(); byte[] proof=sign(value,"unlock-account"); rejected(()->verify(value,binding,body,"unlock-account",proof,NOW-1));
        for(long now:new long[]{-1,ProtocolJson.MAX_INTEGER-59,ProtocolJson.MAX_INTEGER+1}) rejected(()->issue(binding,body,now));
        var max=issue(binding,body,ProtocolJson.MAX_INTEGER-60); assertEquals(ProtocolJson.MAX_INTEGER,max.get("expiresAt")); byte[] maxProof=sign(max,"unlock-account");
        verify(max,binding,body,"unlock-account",maxProof,ProtocolJson.MAX_INTEGER-1); rejected(()->verify(max,binding,body,"unlock-account",maxProof,ProtocolJson.MAX_INTEGER));
        verify(issue(binding,body,0),binding,body,"unlock-account",sign(issue(binding,body,0),"unlock-account"),0);
    }
    @Test void GivenInvalidProofLengthScalarsOrOldKey_WhenVerified_ThenRejected() {
        var value=issue(); byte[] overflow=new byte[64]; Arrays.fill(overflow,(byte)255);
        for(byte[] proof:List.of(new byte[0],new byte[63],new byte[64],overflow,new byte[65])) rejected(()->verify(value,binding,body,"unlock-account",proof,NOW));
        rejected(()->verify(value,binding,body,"unlock-account",sign(value,"recovery-1"),NOW));
    }
    @Test void GivenMalformedRawBody_WhenIssued_ThenRejected() {
        for(String raw:List.of("","[]","null","{}{}","{\"x\":1,\"x\":2}","{\"x\":{\"y\":1,\"y\":2}}","\ufeff{}","{\"x\":\"\\ud800\"}","{\"x\":1.0}","{\"x\":1e0}","{\"x\":NaN}","{\"x\":9007199254740992}")) rejected(()->issue(binding,ProtocolJson.utf8(raw),NOW));
        rejected(()->issue(binding,new byte[]{(byte)255},NOW)); rejected(()->issue(binding,new byte[]{31,(byte)139},NOW));
    }
    @Test void GivenRequestAtAggregateBound_WhenIssued_ThenExactlyOneMiBAccepted() {
        String prefix="{\"idempotencyKey\":\"fixture\",\"value\":\"",suffix="\"}";
        byte[] raw=ProtocolJson.utf8(prefix+"a".repeat(ProtocolJson.MAX_REQUEST-prefix.length()-suffix.length())+suffix);
        var value=issue(binding,raw,NOW); verify(value,binding,raw,"unlock-account",sign(value,"unlock-account"),NOW);
        byte[] larger=Arrays.copyOf(raw,raw.length+1); larger[larger.length-1]=32; rejected(()->issue(binding,larger,NOW));
    }
    @Test void GivenMalformedChallengeSchema_WhenSignedOrVerified_ThenRejected() {
        var original=issue();
        for(var change:Map.<String,Object>of("keyEpoch",true,"issuedAt",true,"expiresAt",NOW+61,"nonce","AA","requestHash","AA","extra",1L).entrySet()) {
            var value=new LinkedHashMap<>(original); value.put(change.getKey(),change.getValue()); rejected(()->bytes(value));
        }
        var missing=new LinkedHashMap<>(original); missing.remove("generation"); rejected(()->bytes(missing));
    }
    @Test void GivenNormalIssuance_WhenRepeated_ThenFreshIdsAndNonces() {
        Class<?>[] types={Map.class,byte[].class,long.class}; var a=map(call("Challenge","issue",types,binding,body,NOW)); var b=map(call("Challenge","issue",types,binding,body,NOW));
        assertNotEquals(a.get("challengeId"),b.get("challengeId")); assertNotEquals(a.get("nonce"),b.get("nonce")); assertFalse(a.containsKey("access")); assertFalse(a.containsKey("session"));
        for(var field:binding.entrySet()) assertEquals(field.getValue(),a.get(field.getKey()));
    }
}
