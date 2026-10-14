import cerberus.protocol.*;
import java.nio.file.*;
import java.util.*;

/** Invoke the independent native verifier; never share .NET cryptographic code. */
public class VerifyServerLeases {
    @SuppressWarnings("unchecked")
    public static void main(String[] args) throws Exception {
        var doc = ProtocolJson.parse(Files.readAllBytes(Path.of(args[0])), Set.of("classification","issuer","pinnedJwk","wrongJwk","cases"), Set.of(), 1048576);
        if (!doc.get("classification").equals("public-test-fixtures")) throw new ProtocolError();
        var trust = new LeaseTrust((String)doc.get("issuer"), (Map<String,Object>)doc.get("pinnedJwk"), 1);
        var wrong = new LeaseTrust((String)doc.get("issuer"), (Map<String,Object>)doc.get("wrongJwk"), 1);
        int count = 0;
        for (var item : (List<Map<String,Object>>)doc.get("cases")) {
            String token = (String)item.get("token"); var expected = (Map<String,Object>)item.get("expected");
            long now = ((Number)item.get("now")).longValue(), seconds = ((Number)item.get("seconds")).longValue();
            var claims = Lease.verify(token, expected, trust, now);
            if (((Number)claims.get("iat")).longValue()!=now || (seconds>0 ? ((Number)claims.get("exp")).longValue()!=now+seconds : claims.containsKey("exp"))) throw new ProtocolError();
            reject(() -> Lease.verify(token, expected, wrong, now));
            reject(() -> Lease.verify(token, expected, trust, now-1));
            var changed = new HashMap<>(expected); changed.put("policyRevision", 99L);
            reject(() -> Lease.verify(token, changed, trust, now));
            var parts = token.split("\\."); byte[] signature = Base64.getUrlDecoder().decode(parts[2]); signature[0]^=1;
            String damaged = parts[0]+"."+parts[1]+"."+Base64.getUrlEncoder().withoutPadding().encodeToString(signature);
            reject(() -> Lease.verify(damaged, expected, trust, now));
            if (seconds>0) reject(() -> Lease.verify(token, expected, trust, now+seconds));
            else Lease.verify(token, expected, trust, now+1000000);
            count++;
        }
        if(count!=4) throw new ProtocolError();
        System.out.println("Java verified four server leases and rejected invalid signatures, trust, binding, future issuance and enabled expiry.");
    }
    private static void reject(Runnable action) {
        try { action.run(); } catch (ProtocolError expected) { return; }
        throw new IllegalStateException("Native verifier accepted a rejected lease.");
    }
}
