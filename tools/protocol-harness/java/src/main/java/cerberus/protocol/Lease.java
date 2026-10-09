package cerberus.protocol;

import java.nio.charset.StandardCharsets;
import java.util.*;

/** Strict original-byte JWS verification, independent of the online clock lifecycle. */
public final class Lease {
    private Lease() {}
    static final Set<String> EXPECTED_FIELDS=Set.of("iss","aud","sub","accountId","scopeKind","scopeId","keyEpoch","protectionRevision","policyRevision","revocationGeneration","grantRevisions","renewalEnabled");
    static final Set<String> CLAIM_FIELDS;
    static { var fields=new HashSet<>(EXPECTED_FIELDS); fields.addAll(Set.of("iat","nbf","jti")); CLAIM_FIELDS=Set.copyOf(fields); }
    static final String TYPE="cerberus-offline-v1+jwt";
    static Map<String,Object> expected(Map<String,Object> expected) {
        var value=Protection.snapshot(expected,EXPECTED_FIELDS);
        if(ProtocolJson.text(value.get("iss"),true).isEmpty() || !"cerberus-offline-clients-v1".equals(value.get("aud"))) throw new ProtocolError();
        for(String field:List.of("sub","accountId","scopeId")) ProtocolJson.guid(ProtocolJson.text(value.get(field),true));
        String kind=ProtocolJson.text(value.get("scopeKind"),true);
        if(!Set.of("account","profile").contains(kind) || kind.equals("account") && !value.get("scopeId").equals(value.get("accountId"))) throw new ProtocolError();
        for(String field:List.of("keyEpoch","protectionRevision","policyRevision","revocationGeneration")) ProtocolJson.integer(value.get(field),1,ProtocolJson.MAX_INTEGER);
        if(!(value.get("renewalEnabled") instanceof Boolean) || !(value.get("grantRevisions") instanceof List<?>)) throw new ProtocolError();
        String previous="";
        for(Object entry:(List<?>)value.get("grantRevisions")) {
            var grant=ProtocolJson.fields(entry,Set.of("grantId","revision"),Set.of()); String id=ProtocolJson.guid(ProtocolJson.text(grant.get("grantId"),true));
            ProtocolJson.integer(grant.get("revision"),1,ProtocolJson.MAX_INTEGER); if(id.compareTo(previous)<=0) throw new ProtocolError(); previous=id;
        }
        return value;
    }
    static Map<String,Object> validate(Map<String,Object> input) {
        ProtocolJson.fields(input,CLAIM_FIELDS,Set.of("exp")); var value=ProtocolJson.parse(Wire.encode(input),CLAIM_FIELDS,Set.of("exp"),ProtocolJson.MAX_REQUEST);
        var authority=new LinkedHashMap<String,Object>(); for(String field:EXPECTED_FIELDS) authority.put(field,value.get(field)); expected(authority);
        long issued=ProtocolJson.integer(value.get("iat"),0,ProtocolJson.MAX_INTEGER); ProtocolJson.guid(ProtocolJson.text(value.get("jti"),true));
        if(ProtocolJson.integer(value.get("nbf"),0,ProtocolJson.MAX_INTEGER)!=issued) throw new ProtocolError();
        if((Boolean)value.get("renewalEnabled")) {
            if(!value.containsKey("exp") || ProtocolJson.integer(value.get("exp"),0,ProtocolJson.MAX_INTEGER)<=issued) throw new ProtocolError();
        } else if(value.containsKey("exp")) throw new ProtocolError();
        return value;
    }
    public static Map<String,Object> claims(Map<String,Object> expected,long issuedAt,boolean renewalEnabled,Long durationSeconds) {
        var value=expected(expected); ProtocolJson.integer(issuedAt,0,ProtocolJson.MAX_INTEGER);
        if(!Boolean.valueOf(renewalEnabled).equals(value.get("renewalEnabled"))) throw new ProtocolError();
        value.put("iat",issuedAt); value.put("nbf",issuedAt); value.put("jti",UUID.randomUUID().toString());
        if(renewalEnabled) { long duration=durationSeconds==null?86400:ProtocolJson.integer(durationSeconds,1,ProtocolJson.MAX_INTEGER); value.put("exp",ProtocolJson.integer(issuedAt+duration,0,ProtocolJson.MAX_INTEGER)); }
        else if(durationSeconds!=null) throw new ProtocolError();
        return validate(value);
    }
    public static String sign(Map<String,Object> claims,byte[] leasePrivateDer) {
        var value=validate(claims); var header=Map.<String,Object>of("alg","ES256","typ",TYPE,"kid",Keys.thumbprint(Keys.publicJwk(leasePrivateDer)));
        String prefix=ProtocolJson.base64(Wire.encode(header))+"."+ProtocolJson.base64(Wire.encode(value)); String token=prefix+"."+ProtocolJson.base64(Keys.sign(leasePrivateDer,prefix.getBytes(StandardCharsets.US_ASCII)));
        if(token.length()>ProtocolJson.MAX_REQUEST) throw new ProtocolError(); return token;
    }
    private static byte[] decode(String encoded) { return ProtocolJson.unbase64(encoded,encoded.length()*3/4); }
    public static Map<String,Object> verify(String compact,Map<String,Object> expected,LeaseTrust trust,long effectiveNow) {
        ProtocolJson.text(compact,true); if(compact.length()>ProtocolJson.MAX_REQUEST || trust==null) throw new ProtocolError();
        String[] parts=compact.split("\\.",-1); if(parts.length!=3) throw new ProtocolError();
        var header=ProtocolJson.parse(decode(parts[0]),Set.of("alg","typ","kid"),Set.of(),ProtocolJson.MAX_REQUEST);
        if(!"ES256".equals(header.get("alg")) || !TYPE.equals(header.get("typ"))) throw new ProtocolError();
        var key=trust.lookup(ProtocolJson.text(header.get("kid"),true)); byte[] signature=ProtocolJson.unbase64(parts[2],64);
        if(!Keys.verify(key,(parts[0]+"."+parts[1]).getBytes(StandardCharsets.US_ASCII),signature)) throw new ProtocolError();
        var value=validate(ProtocolJson.parse(decode(parts[1]),CLAIM_FIELDS,Set.of("exp"),ProtocolJson.MAX_REQUEST)); var authority=expected(expected); ProtocolJson.integer(effectiveNow,0,ProtocolJson.MAX_INTEGER);
        if(!authority.get("iss").equals(trust.issuer)) throw new ProtocolError(); for(String field:EXPECTED_FIELDS) if(!Objects.equals(authority.get(field),value.get(field))) throw new ProtocolError();
        if(effectiveNow<(Long)value.get("iat") || (Boolean)value.get("renewalEnabled") && effectiveNow>=(Long)value.get("exp")) throw new ProtocolError();
        return value;
    }
}
