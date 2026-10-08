package cerberus.protocol;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import com.fasterxml.jackson.core.*;
import com.fasterxml.jackson.core.json.JsonWriteFeature;
import org.bouncycastle.crypto.generators.Argon2BytesGenerator;
import org.bouncycastle.crypto.params.Argon2Parameters;
import org.bouncycastle.crypto.hpke.HPKE;

/** Independently constructed Java fixtures, never Python validation or crypto. */
public final class FixtureCases {
    private FixtureCases() {}
    static final Path FIXTURES=Path.of("tools/protocol-harness/fixtures");
    static final int LIMIT=64*1024*1024;
    static final String ACCOUNT="00000000-0000-0000-0000-000000000001",IDENTITY="00000000-0000-0000-0000-000000000002",OTHER="00000000-0000-0000-0000-000000000008",ISSUER="https://cerberus.example.test",PASSWORD=" vault-é ";
    static final long NOW=1700000000;
    static final byte[] RAW="{\"idempotencyKey\":\"fixture\",\"expectedRevision\":1}".getBytes(StandardCharsets.UTF_8),ROOT=sequence(0,32),SECRET=sequence(32,32);
    static byte[] sequence(int start,int size) { byte[] value=new byte[size]; for(int i=0;i<size;i++) value[i]=(byte)(start+i); return value; }
    @SuppressWarnings("unchecked") static Map<String,Object> map(Object value) { if(!(value instanceof Map<?,?>)) throw new ProtocolError(); return (Map<String,Object>)value; }
    static Map<String,Object> read(Path path) {
        try { if(Files.size(path)>LIMIT) throw new ProtocolError(); return ProtocolJson.object(Files.readAllBytes(path),LIMIT); }
        catch(IOException ex) { throw new ProtocolError(); }
    }
    private static void json(JsonGenerator generator,Object value) throws IOException {
        if(value==null) generator.writeNull(); else if(value instanceof Boolean v) generator.writeBoolean(v); else if(value instanceof String v) generator.writeString(v);
        else if(value instanceof Number v) generator.writeNumber(v.longValue());
        else if(value instanceof List<?> list) { generator.writeStartArray(); for(Object entry:list) json(generator,entry); generator.writeEndArray(); }
        else if(value instanceof Map<?,?> object) { generator.writeStartObject(); var sorted=new TreeMap<String,Object>(); for(var entry:object.entrySet()) sorted.put((String)entry.getKey(),entry.getValue()); for(var entry:sorted.entrySet()) { generator.writeFieldName(entry.getKey()); json(generator,entry.getValue()); } generator.writeEndObject(); }
        else throw new ProtocolError();
    }
    static byte[] bytes(Map<String,Object> value) {
        try { var out=new ByteArrayOutputStream(); var factory=JsonFactory.builder().disable(JsonWriteFeature.WRITE_HEX_UPPER_CASE).build(); try(var generator=factory.createGenerator(out)) { json(generator,value); } if(out.size()>LIMIT) throw new ProtocolError(); return out.toByteArray(); }
        catch(IOException ex) { throw new ProtocolError(); }
    }
    static void write(Path path,Map<String,Object> value) { try { Files.write(path,bytes(value)); } catch(IOException ex) { throw new ProtocolError(); } }
    static String digest(Map<String,Object> value) { return HexFormat.of().formatHex(Primitives.sha256(bytes(value))); }
    static Map<String,Object> copy(Map<String,Object> value) { return ProtocolJson.object(bytes(value),LIMIT); }
    static Map<String,Object> document(String field,List<?> rows) { return Map.of("schemaVersion",1L,"classification","public-test-fixtures","implementation","java","version","1.0.0",field,rows); }
    static Map<String,Object> input() { var value=read(FIXTURES.resolve("input.json")); if(!"public-test-fixtures-never-production".equals(value.get("classification"))) throw new ProtocolError(); return value; }
    static Map<String,Object> jwk(String role) { return map(map(map(input().get("keys")).get(role)).get("publicJwk")); }
    static byte[] privateKey(String role) { return Base64.getUrlDecoder().decode((String)map(map(input().get("keys")).get(role)).get("privateDer")); }
    static Map<String,Object> bundle(String scope) { return map(map(input().get("bundles")).get(scope)); }
    static Set<String> membership(String scope) { var values=new HashSet<String>(); for(Object entry:(List<?>)map(input().get("membership")).get(scope)) values.add((String)entry); return values; }
    static Context slot(String kind,String scope) { String id=Set.of("account","profile").contains(scope)?(String)bundle(scope).get("scopeId"):"00000000-0000-0000-0000-000000000004"; return new Context(ACCOUNT,kind,id,1,List.of()); }
    static Map<String,Object> authority(boolean enabled) { var value=new LinkedHashMap<String,Object>(); value.put("iss",ISSUER); value.put("aud","cerberus-offline-clients-v1"); value.put("sub",IDENTITY); value.put("accountId",ACCOUNT); value.put("scopeKind","account"); value.put("scopeId",ACCOUNT); for(String field:List.of("keyEpoch","protectionRevision","policyRevision","revocationGeneration")) value.put(field,1L); value.put("grantRevisions",List.of()); value.put("renewalEnabled",enabled); return value; }
    static String role(String operation) { return operation.equals("recover")?"recovery-1":operation.equals("unlock-profile")?"unlock-profile":"unlock-account"; }
    static Map<String,Object> binding(String operation) { var value=new LinkedHashMap<String,Object>(); value.put("operation",operation); value.put("identityId",IDENTITY); value.put("accountId",ACCOUNT); value.put("scopeKind",operation.equals("unlock-profile")?"profile":"account"); value.put("scopeId",operation.equals("unlock-profile")?bundle("profile").get("scopeId"):ACCOUNT); value.put("keyEpoch",1L); value.put("protectionRevision",1L); value.put("generation",operation.equals("recover")?1L:null); return value; }
    static Map<String,Object> base(String family) {
        String[] p=family.split("\\."); String kind=p[0],name=p[1];
        return switch(kind) {
            case "content" -> Symmetric.seal(ROOT,slot(name,name),"cerberus-content-v1","public fixture plaintext".getBytes(StandardCharsets.UTF_8),new NonceGuard(0));
            case "password" -> Protection.wrap(PASSWORD,bundle(name),slot(name+"-protection",name),new NonceGuard(0));
            case "recovery" -> RecoveryBundle.wrap(SECRET,bundle("account"),privateKey("recovery-1"),slot("recovery","account"),1,new NonceGuard(0));
            case "recipient" -> Recipient.wrap(ROOT,slot("record","record"),"00000000-0000-0000-0000-000000000007",1,IDENTITY,jwk("recipient"),privateKey("author"));
            case "challenge" -> { var issued=Challenge.issue(binding(name),RAW,NOW); yield Map.of("challenge",issued,"proof",ProtocolJson.base64(Challenge.sign(issued,privateKey(role(name)))),"raw",ProtocolJson.base64(RAW)); }
            case "lease" -> Map.of("token",Lease.sign(Lease.claims(authority(name.equals("enabled")),NOW,name.equals("enabled"),null),privateKey("lease")));
            case "encoding" -> Map.of("bytes",ProtocolJson.base64(ProtocolJson.context(Arrays.asList("cerberus-fixture-v1",ACCOUNT,1L,true,null,List.of()))));
            case "kdf" -> Map.of("bytes",ProtocolJson.base64(Primitives.argon2(PASSWORD.getBytes(StandardCharsets.UTF_8),sequence(0,16))));
            case "hkdf" -> Map.of("bytes",ProtocolJson.base64(Primitives.hkdf(ROOT,new byte[32],"cerberus-fixture-v1".getBytes(StandardCharsets.UTF_8),32)));
            case "signature" -> Map.of("bytes",ProtocolJson.base64(Keys.sign(privateKey("author"),"public fixture signature".getBytes(StandardCharsets.UTF_8))));
            case "clock" -> clock(name);
            case "model" -> model(name);
            default -> throw new ProtocolError();
        };
    }
    static Object changed(Object value) { if(value==null) return 1L; if(value instanceof Boolean v) return !v; if(value instanceof Number) return true; if(value instanceof Map<?,?>) return Map.of(); if(value instanceof List<?>) return Arrays.asList((Object)null); return "AA"; }
    static byte[] decode(String value) { return ProtocolJson.unbase64(value,value.length()*3/4); }
    static String rawToken(Map<String,Object> payload,Map<String,Object> header) { String prefix=ProtocolJson.base64(bytes(header))+"."+ProtocolJson.base64(bytes(payload)); return prefix+"."+ProtocolJson.base64(Keys.sign(privateKey("lease"),prefix.getBytes(StandardCharsets.US_ASCII))); }
    static Map<String,Object> produce(Map<String,Object> inputs) {
        if(!inputs.equals(input())) throw new ProtocolError(); List<Object> rows=new ArrayList<>(); Map<String,Map<String,Object>> bases=new HashMap<>();
        for(Object entry:(List<?>)read(FIXTURES.resolve("case-manifest.json")).get("cases")) {
            var rule=map(entry); String id=(String)rule.get("id"); String[] p=id.split("\\."); String kind=p[0],family=p[0]+"."+p[1];
            var output=copy(bases.computeIfAbsent(family,x->p[1].equals("invalid")?Map.of():base(x)));
            if(kind.equals("encoding") && p[1].equals("invalid")) {
                String bad=switch(p[2]) { case "duplicate" -> "{\"x\":1,\"x\":2}"; case "nested-duplicate" -> "{\"x\":{\"y\":1,\"y\":2}}"; case "bom" -> "\ufeff{}"; case "unicode" -> "{\"x\":\"\\ud800\"}"; case "decimal" -> "{\"x\":1.0}"; case "exponent" -> "{\"x\":1e0}"; case "overflow" -> "{\"x\":9007199254740992}"; case "over-limit" -> " ".repeat(ProtocolJson.MAX_REQUEST)+"{}"; default -> throw new ProtocolError(); };
                output.put("raw",ProtocolJson.base64(bad.getBytes(StandardCharsets.UTF_8)));
            } else if(p.length>2 && p[2].equals("mutate")) {
                Map<String,Object> target=kind.equals("challenge")?map(output.get("challenge")):output,header=null;
                if(kind.equals("lease")) { String[] token=((String)output.get("token")).split("\\."); header=ProtocolJson.object(decode(token[0]),ProtocolJson.MAX_REQUEST); target=ProtocolJson.object(decode(token[1]),ProtocolJson.MAX_REQUEST); }
                if(p[3].equals("extra")) target.put("extra",1L); else if(p[3].equals("missing")) target.remove(new TreeSet<>(target.keySet()).first()); else target.put(p[3],changed(target.get(p[3])));
                if(kind.equals("lease")) output=new LinkedHashMap<>(Map.of("token",rawToken(target,header)));
            } else if(p.length>2 && p[2].equals("kdf")) { var kdf=map(output.get("kdf")); kdf.put(p[3],p[3].equals("extra")?1L:changed(kdf.get(p[3]))); }
            else if(p.length>2 && p[2].equals("header")) {
                String[] token=((String)output.get("token")).split("\\."); var header=ProtocolJson.object(decode(token[0]),ProtocolJson.MAX_REQUEST); var payload=ProtocolJson.object(decode(token[1]),ProtocolJson.MAX_REQUEST); String field=p[3],compact;
                if(field.equals("duplicate")) { String raw=new String(bytes(header),StandardCharsets.UTF_8); String prefix=ProtocolJson.base64((raw.substring(0,raw.length()-1)+",\"alg\":\"ES256\"}").getBytes(StandardCharsets.UTF_8))+"."+token[1]; compact=prefix+"."+ProtocolJson.base64(Keys.sign(privateKey("lease"),prefix.getBytes(StandardCharsets.UTF_8))); }
                else {
                    switch(field) { case "expiry-policy" -> { if(payload.containsKey("exp")) payload.remove("exp"); else payload.put("exp",NOW+86400); } case "alg-none" -> header.put("alg","none"); case "alg-hs256" -> header.put("alg","HS256"); case "typ" -> header.put("typ","JWT"); case "kid" -> header.put("kid","unknown"); case "jku" -> header.put("jku",ISSUER); case "jwk" -> header.put("jwk",jwk("author")); case "crit" -> header.put("crit",List.of("x")); default -> throw new ProtocolError(); }
                    compact=rawToken(payload,header);
                } output=new LinkedHashMap<>(Map.of("token",compact));
            } else if(p.length>2 && p[2].equals("scenario") && p[3].equals("reordered-body")) output.put("raw",ProtocolJson.base64("{\"expectedRevision\":1,\"idempotencyKey\":\"fixture\"}".getBytes(StandardCharsets.UTF_8)));
            var row=new LinkedHashMap<>(rule); row.put("output",output); row.put("digest",digest(output)); row.put("deterministic",Set.of("encoding","kdf","hkdf","signature","clock","model").contains(kind)); rows.add(row);
        } return document("cases",rows);
    }
    static void equal(Object a,Object b) { if(!Objects.deepEquals(a,b)) throw new ProtocolError(); }
    static void execute(Map<String,Object> test) {
        String[] p=((String)test.get("id")).split("\\."); String kind=p[0],name=p[1]; var output=map(test.get("output"));
        if(Set.of("content","password","recovery","recipient").contains(kind)) {
            var ctx=kind.equals("content")?slot(name,name):kind.equals("password")?slot(name+"-protection",name):kind.equals("recovery")?slot("recovery","account"):slot("record","record");
            if(p.length>2 && p[2].equals("binding")) ctx=new Context(p[3].equals("owner")?OTHER:ctx.ownerId(),ctx.resourceKind(),p[3].equals("resource")?OTHER:ctx.resourceId(),p[3].equals("epoch")?2:1,List.of());
            switch(kind) {
                case "content" -> equal("public fixture plaintext".getBytes(StandardCharsets.UTF_8),Symmetric.open(ROOT,ctx,"cerberus-content-v1",output));
                case "password" -> equal(bundle(name),Protection.unwrap(PASSWORD,output,ctx,membership(name),jwk("unlock-"+name)));
                case "recovery" -> equal(bundle("account"),RecoveryBundle.unwrap(SECRET,output,ctx,1,jwk("recovery-1"),membership("account"),jwk("unlock-account")).get("protectionBundle"));
                case "recipient" -> equal(ROOT,Recipient.open(output,ctx,"00000000-0000-0000-0000-000000000007",1,IDENTITY,privateKey("recipient"),new ClientTrust(ACCOUNT,jwk("recipient"),jwk("author"),1)));
            }
        } else if(kind.equals("challenge")) { String last=p[p.length-1]; long now=last.equals("expiry")?NOW+60:last.equals("future")?NOW-1:NOW; Challenge.verify(map(output.get("challenge")),binding(name),decode((String)output.get("raw")),jwk(last.equals("wrong-key")?"lease":role(name)),ProtocolJson.unbase64((String)output.get("proof"),64),now); }
        else if(kind.equals("lease")) Lease.verify((String)output.get("token"),authority(name.equals("enabled")),new LeaseTrust(ISSUER,jwk("lease"),1),NOW);
        else if(kind.equals("encoding") && name.equals("invalid")) ProtocolJson.object(decode((String)output.get("raw")),ProtocolJson.MAX_REQUEST);
        else equal(base(kind+"."+name),output);
    }
    static Map<String,Object> consume(Map<String,Object> input) {
        if(!"public-test-fixtures".equals(input.get("classification"))) throw new ProtocolError(); var rules=new HashMap<String,Map<String,Object>>(); for(Object entry:(List<?>)read(FIXTURES.resolve("case-manifest.json")).get("cases")) { var rule=map(entry); rules.put((String)rule.get("id"),rule); }
        List<Object> rows=new ArrayList<>(); var seen=new HashSet<String>(); if(((List<?>)input.get("cases")).size()!=rules.size()) throw new ProtocolError();
        for(Object entry:(List<?>)input.get("cases")) {
            var test=map(entry); String id=(String)test.get("id"); var rule=rules.get(id); if(rule==null || !seen.add(id) || !Objects.equals(test.get("kind"),rule.get("kind")) || !Objects.equals(test.get("expect"),rule.get("expect")) || !digest(map(test.get("output"))).equals(test.get("digest"))) throw new ProtocolError();
            String observed="success"; try { execute(test); } catch(ProtocolError ex) { if(!ex.code.equals("invalid_protocol")) throw ex; observed="invalid_protocol"; }
            rows.add(Map.of("id",id,"status",observed.equals(rule.get("expect"))?"pass":"fail","digest",test.get("digest")));
        } return document("results",rows);
    }
    static Map<String,Object> clock(String name) {
        long[] wall={NOW},mono={100}; var c=new OfflineClock(()->wall[0],()->mono[0]); var value=Lease.claims(authority(true),NOW,true,null); c.renew(value,true); wall[0]+=10; mono[0]+=10; long before=c.effectiveNow();
        if(name.equals("elapsed")) return Map.of("effectiveNow",before); if(name.equals("wall-rollback")) wall[0]--; else if(name.equals("monotonic-rollback")) mono[0]--; else { c.restart(); if(name.equals("import")) Lease.verify(Lease.sign(value,privateKey("lease")),authority(true),new LeaseTrust(ISSUER,jwk("lease"),1),NOW+10); }
        try { c.effectiveNow(); } catch(ProtocolError ex) { return Map.of("effectiveNow",before,"blocked",true); } throw new ProtocolError();
    }
    static Map<String,Object> model(String name) {
        var envelope=new LinkedHashMap<String,Object>(); envelope.put("keyEpoch",1L); envelope.put("keySalt",ProtocolJson.base64(new byte[32])); envelope.put("nonce",ProtocolJson.base64(new byte[12])); envelope.put("ciphertext","AA"); envelope.put("tag",ProtocolJson.base64(new byte[16]));
        var password=new LinkedHashMap<>(envelope); password.put("format",Protection.FORMAT); password.put("kdf",Map.of("algorithm","argon2id-v1.3","memoryKiB",65536L,"iterations",3L,"parallelism",4L,"salt",ProtocolJson.base64(new byte[16])));
        var recovery=new LinkedHashMap<>(envelope); recovery.put("format",RecoveryBundle.FORMAT); recovery.put("generation",1L); recovery.put("proofKeyFingerprint",Keys.thumbprint(jwk("recovery-1")));
        var state=new LinkedHashMap<String,Object>(); state.put("accountId",ACCOUNT); state.put("identityId",IDENTITY); state.put("scopeKind","account"); state.put("scopeId",ACCOUNT); for(String field:List.of("keyEpoch","protectionRevision","generation","revocationGeneration")) state.put(field,1L); state.put("unlockVerifier",jwk("unlock-account")); state.put("recoveryVerifier",jwk("recovery-1")); state.put("passwordWrapper",password); state.put("recoveryWrapper",recovery);
        var model=new RecoveryModel(state,()->NOW); password.put("keyEpoch",2L); recovery.put("keyEpoch",2L); recovery.put("generation",2L); recovery.put("proofKeyFingerprint",Keys.thumbprint(jwk("recovery-2")));
        String operation=name.equals("refresh")?"refresh-recovery":"recover"; byte[] raw=Wire.encode(Map.of("operation",operation,"idempotencyKey","fixture","expectedRevision",1L,"passwordWrapper",password,"recoveryWrapper",recovery,"newRecoveryVerifier",jwk("recovery-2"))); var issued=Challenge.issue(binding(operation),raw,NOW); model.addChallenge(issued); byte[] proof=Challenge.sign(issued,privateKey(role(operation))); String id=(String)issued.get("challengeId");
        if(name.equals("rollback")) { var before=model.snapshot(); try { model.execute(IDENTITY,true,raw,id,proof,true,true); } catch(ProtocolError ex) { equal(before,model.snapshot()); return Map.of("rolledBack",true); } throw new ProtocolError(); }
        var outcome=model.execute(IDENTITY,true,raw,id,proof,true,false); if(name.equals("retry")) { var after=model.snapshot(); equal(outcome,model.execute(IDENTITY,true,raw,id,proof,true,false)); equal(after,model.snapshot()); } return outcome;
    }
    static byte[] hex(Map<String,Object> value,String field) { return HexFormat.of().parseHex((String)value.get(field)); }
    static Map<String,Object> selfTest() {
        var v=read(FIXTURES.resolve("known-answers.json")); var h=map(v.get("hkdf")); equal(hex(h,"okm"),Primitives.hkdf(hex(h,"ikm"),hex(h,"salt"),hex(h,"info"),((Number)h.get("length")).intValue()));
        for(Object entry:(List<?>)map(v.get("gcm")).get("cases")) { var g=map(entry); equal(HexFormat.of().parseHex((String)g.get("CT")+(String)g.get("Tag")),Primitives.gcmSeal(hex(g,"Key"),hex(g,"IV"),hex(g,"AAD"),hex(g,"PT"))); }
        var e=map(v.get("ecdsa")); equal(hex(e,"signature"),Keys.sign(privateKey("author"),((String)e.get("message")).getBytes(StandardCharsets.UTF_8))); var t=map(v.get("thumbprint")); equal(t.get("value"),ProtocolJson.base64(Primitives.sha256(((String)t.get("canonical")).getBytes(StandardCharsets.UTF_8))));
        var a=map(v.get("argon2")); var generator=new Argon2BytesGenerator(); generator.init(new Argon2Parameters.Builder(Argon2Parameters.ARGON2_id).withVersion(Argon2Parameters.ARGON2_VERSION_13).withMemoryAsKB(32).withIterations(3).withParallelism(4).withSalt(hex(a,"salt")).withSecret(hex(a,"secret")).withAdditional(hex(a,"ad")).build()); byte[] result=new byte[32]; generator.generateBytes(hex(a,"password"),result); equal(hex(a,"tag"),result);
        var hp=map(v.get("hpke")); var suite=new HPKE(HPKE.mode_base,HPKE.kem_P256_SHA256,HPKE.kdf_HKDF_SHA256,HPKE.aead_AES_GCM256); var receiver=suite.setupBaseR(hex(hp,"enc"),suite.deserializePrivateKey(hex(hp,"skRm"),hex(hp,"pkRm")),hex(hp,"info")); var c=map(((List<?>)hp.get("encryptions")).getFirst()); try { equal(hex(c,"pt"),receiver.open(hex(c,"aad"),hex(c,"ct"))); } catch(Exception ex) { throw new ProtocolError(); }
        List<Object> rows=new ArrayList<>(); for(String id:List.of("hkdf","gcm","ecdsa","thumbprint","argon2","hpke")) rows.add(Map.of("id",id,"status","pass")); return document("results",rows);
    }
}
