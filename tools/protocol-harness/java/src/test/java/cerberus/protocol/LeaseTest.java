package cerberus.protocol;

import java.lang.reflect.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;
import static cerberus.protocol.Fixtures.*;

class LeaseTest {
    static final long NOW=1700000000;
    static final String ISSUER="https://cerberus.example.test",ACCOUNT="00000000-0000-0000-0000-000000000001",IDENTITY="00000000-0000-0000-0000-000000000002",OTHER="00000000-0000-0000-0000-000000000008";
    Object trust;
    @BeforeEach void setup() { trust=createTrust(); assertDoesNotThrow(()->Class.forName("cerberus.protocol.Lease"),"Lease missing"); }
    static Object createTrust() { return assertDoesNotThrow(()->Class.forName("cerberus.protocol.LeaseTrust").getConstructor(String.class,Map.class,long.class).newInstance(ISSUER,publicJwk("lease"),1L),"LeaseTrust missing"); }
    static Object invoke(Object instance,String method,Class<?>[] types,Object... args) {
        try { return instance.getClass().getMethod(method,types).invoke(instance,args); }
        catch(InvocationTargetException ex) { if(ex.getCause() instanceof RuntimeException r) throw r; throw new AssertionError(ex.getCause()); }
        catch(ReflectiveOperationException ex) { throw new AssertionError(ex); }
    }
    static Map<String,Object> expected(boolean enabled) {
        var value=new LinkedHashMap<String,Object>(); value.put("iss",ISSUER); value.put("aud","cerberus-offline-clients-v1"); value.put("sub",IDENTITY); value.put("accountId",ACCOUNT); value.put("scopeKind","account"); value.put("scopeId",ACCOUNT);
        for(String field:List.of("keyEpoch","protectionRevision","policyRevision","revocationGeneration")) value.put(field,1L);
        value.put("grantRevisions",List.of()); value.put("renewalEnabled",enabled); return value;
    }
    static Map<String,Object> claims(Map<String,Object> expected,long issued,boolean enabled,Long duration) { return map(call("Lease","claims",new Class<?>[]{Map.class,long.class,boolean.class,Long.class},expected,issued,enabled,duration)); }
    static String sign(Map<String,Object> claims) { return (String)call("Lease","sign",new Class<?>[]{Map.class,byte[].class},claims,privateDer("lease")); }
    static Map<String,Object> verify(String token,Map<String,Object> expected,Object trust,long now) {
        return map(call("Lease","verify",new Class<?>[]{String.class,Map.class,trust.getClass(),long.class},token,expected,trust,now));
    }
    static Map<String,Object> header() { return Map.of("alg","ES256","typ","cerberus-offline-v1+jwt","kid",Keys.thumbprint(publicJwk("lease"))); }
    static String compact(byte[] payload,byte[] header) {
        String prefix=ProtocolJson.base64(header)+"."+ProtocolJson.base64(payload); return prefix+"."+ProtocolJson.base64(Keys.sign(privateDer("lease"),prefix.getBytes(StandardCharsets.US_ASCII)));
    }
    static String compact(Map<String,Object> claims) { return compact(Wire.encode(claims),Wire.encode(header())); }
    Map<String,Object> claims() { return claims(expected(true),NOW,true,null); }
    void reject(String token) { rejected(()->verify(token,expected(true),trust,NOW)); }
    @Test void GivenEnabledOrDisabledPolicy_WhenSigned_ThenExactClaimsVerified() {
        for(boolean enabled:List.of(true,false)) { var expected=expected(enabled); var claims=claims(expected,NOW,enabled,null); assertEquals(claims,verify(sign(claims),expected,trust,NOW)); assertEquals(enabled,claims.containsKey("exp")); if(enabled) assertEquals(NOW+86400,claims.get("exp")); }
    }
    @Test void GivenRepresentableDurations_WhenIssued_ThenNoInventedMaximum() {
        for(long duration:List.of(1L,86400L,1000000000000L,ProtocolJson.MAX_INTEGER-NOW)) assertEquals(NOW+duration,claims(expected(true),NOW,true,duration).get("exp"));
        for(long duration:List.of(0L,-1L,ProtocolJson.MAX_INTEGER,ProtocolJson.MAX_INTEGER+1)) rejected(()->claims(expected(true),NOW,true,duration));
        rejected(()->claims(expected(true),ProtocolJson.MAX_INTEGER,true,null)); rejected(()->claims(expected(false),NOW,false,86400L)); rejected(()->claims(expected(false),NOW,true,null));
    }
    @Test void GivenMissingOrForbiddenExpiry_WhenVerified_ThenRejected() {
        var enabled=claims(); enabled.remove("exp"); reject(compact(enabled)); var disabled=claims(expected(false),NOW,false,null); disabled.put("exp",NOW+1); rejected(()->verify(compact(disabled),expected(false),trust,NOW));
        reject(compact(claims(expected(false),NOW,false,null)));
    }
    @Test void GivenFutureNotBeforeOrExclusiveExpiry_WhenVerified_ThenRejected() {
        String token=compact(claims(expected(true),NOW,true,1L)); verify(token,expected(true),trust,NOW);
        for(long now:List.of(NOW-1,NOW+1,-1L,ProtocolJson.MAX_INTEGER+1)) rejected(()->verify(token,expected(true),trust,now)); var claims=claims(); claims.put("nbf",NOW-1); reject(compact(claims));
    }
    @Test void GivenEveryExpectedClaimChanged_WhenVerified_ThenRejected() {
        var changes=new LinkedHashMap<String,Object>(); changes.put("iss","https://wrong.test"); changes.put("aud","wrong"); changes.put("sub",OTHER); changes.put("accountId",OTHER); changes.put("scopeKind","profile"); changes.put("scopeId",OTHER);
        for(String field:List.of("keyEpoch","protectionRevision","policyRevision","revocationGeneration")) changes.put(field,2L);
        changes.put("grantRevisions",List.of(Map.of("grantId",OTHER,"revision",1L))); changes.put("renewalEnabled",false);
        for(var change:changes.entrySet()) { var claims=claims(); claims.put(change.getKey(),change.getValue()); reject(compact(claims)); }
    }
    @Test void GivenReorderedPropertyBytes_WhenVerified_ThenOriginalSigningInputUsed() {
        var claims=claims(); List<String> names=new ArrayList<>(claims.keySet()); Collections.reverse(names); StringJoiner json=new StringJoiner(",","{","}");
        for(String key:names) { String encoded=new String(Wire.encode(Collections.singletonMap(key,claims.get(key))),StandardCharsets.UTF_8); json.add(encoded.substring(1,encoded.length()-1)); }
        byte[] reordered=json.toString().getBytes(StandardCharsets.UTF_8); assertEquals(claims,verify(compact(reordered,Wire.encode(header())),expected(true),trust,NOW));
        String[] token=compact(claims).split("\\."); token[1]=ProtocolJson.base64(reordered); reject(String.join(".",token));
    }
    @Test void GivenUntrustedHeaderAlgorithmsKeysOrExtensions_WhenVerified_ThenRejected() {
        var changes=new LinkedHashMap<String,Object>(); changes.put("alg","none"); changes.put("typ","JWT"); changes.put("kid",Keys.thumbprint(publicJwk("author"))); changes.put("jku",ISSUER); changes.put("jwk",publicJwk("lease")); changes.put("x5u",ISSUER); changes.put("crit",List.of("x")); changes.put("extra",1L);
        for(var change:changes.entrySet()) { var changed=new LinkedHashMap<>(header()); changed.put(change.getKey(),change.getValue()); reject(compact(Wire.encode(claims()),Wire.encode(changed))); }
        for(String alg:List.of("HS256","ES384")) { var changed=new LinkedHashMap<>(header()); changed.put("alg",alg); reject(compact(Wire.encode(claims()),Wire.encode(changed))); }
    }
    @Test void GivenMalformedCompactDuplicatesUnknownOrNumericClaims_WhenVerified_ThenRejected() {
        String good=compact(claims()); for(String token:List.of("","a.b",good+".extra",good+"=",good.substring(0,good.length()-1)+"!",good.substring(0,good.lastIndexOf('.')+1)+ProtocolJson.base64(new byte[64]),good+"x")) reject(token);
        for(var change:Map.<String,Object>of("keyEpoch",true,"iat",true,"jti","00000000-0000-0000-0000-000000000000","extra",1L).entrySet()) { var claims=claims(); claims.put(change.getKey(),change.getValue()); reject(compact(claims)); }
        String raw=new String(Wire.encode(claims()),StandardCharsets.UTF_8); for(String bad:List.of("\ufeff"+raw,raw.substring(0,raw.length()-1)+",\"iat\":1700000000}",raw.replace("\"keyEpoch\":1","\"keyEpoch\":1e0"))) reject(compact(bad.getBytes(StandardCharsets.UTF_8),Wire.encode(header())));
        reject(compact(Wire.encode(claims()),"{\"alg\":\"ES256\",\"alg\":\"ES256\"}".getBytes(StandardCharsets.UTF_8)));
    }
    @Test void GivenGrantSortingDuplicatesOrScopeMismatch_WhenUsed_ThenRejected() {
        var expected=expected(true); var grants=List.of(Map.of("grantId",IDENTITY,"revision",1L),Map.of("grantId",OTHER,"revision",2L)); expected.put("grantRevisions",grants); verify(compact(claims(expected,NOW,true,null)),expected,trust,NOW);
        for(Object invalid:List.of(List.of(grants.get(1),grants.get(0)),List.of(grants.get(0),grants.get(0)),List.of(Map.of("grantId",IDENTITY,"revision",true)),List.of(Map.of("grantId",IDENTITY,"revision",1L,"extra",2L)))) { var changed=new LinkedHashMap<>(expected); changed.put("grantRevisions",invalid); rejected(()->claims(changed,NOW,true,null)); }
        var invalid=expected(true); invalid.put("scopeId",OTHER); rejected(()->claims(invalid,NOW,true,null));
    }
    @Test void GivenTrustedOldLeaseKey_WhenRotated_ThenOldVerificationRetained() {
        var replacement=publicJwk("author"); String old=Keys.thumbprint(publicJwk("lease")),fresh=Keys.thumbprint(replacement);
        byte[] signature=Keys.sign(privateDer("lease"),ProtocolJson.context(List.of("cerberus-lease-key-transition-v1",ISSUER,old,fresh,2L)));
        invoke(trust,"transition",new Class<?>[]{Map.class,long.class,byte[].class},replacement,2L,signature);
        assertEquals(publicJwk("lease"),invoke(trust,"lookup",new Class<?>[]{String.class},old)); assertEquals(replacement,invoke(trust,"lookup",new Class<?>[]{String.class},fresh)); verify(compact(claims()),expected(true),trust,NOW);
        rejected(()->invoke(trust,"transition",new Class<?>[]{Map.class,long.class,byte[].class},replacement,2L,signature)); rejected(()->invoke(trust,"lookup",new Class<?>[]{String.class},"unknown"));
    }
    @Test void GivenWrongDomainIssuerNewKeyOrRevision_WhenRotated_ThenRejected() {
        var replacement=publicJwk("author"); String old=Keys.thumbprint(publicJwk("lease")),fresh=Keys.thumbprint(replacement);
        for(var values:List.of(List.of("cerberus-key-transition-v1",ISSUER,2L,"lease"),List.of("cerberus-lease-key-transition-v1","wrong",2L,"lease"),List.of("cerberus-lease-key-transition-v1",ISSUER,1L,"lease"),List.of("cerberus-lease-key-transition-v1",ISSUER,2L,"author"))) {
            byte[] signature=Keys.sign(privateDer((String)values.get(3)),ProtocolJson.context(List.of(values.get(0),values.get(1),old,fresh,values.get(2)))); rejected(()->invoke(trust,"transition",new Class<?>[]{Map.class,long.class,byte[].class},replacement,values.get(2),signature));
        }
    }
}
