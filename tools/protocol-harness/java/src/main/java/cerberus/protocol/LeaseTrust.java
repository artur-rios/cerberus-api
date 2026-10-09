package cerberus.protocol;

import java.util.*;

/** Lease-only trust domain. Old verification pins remain for outstanding leases. */
public final class LeaseTrust {
    final String issuer;
    private final Map<String,Map<String,Object>> keys=new HashMap<>();
    private String current;
    private long revision;
    public LeaseTrust(String issuer,Map<String,Object> pinnedJwk,long revision) {
        this.issuer=ProtocolJson.text(issuer,true); if(issuer.isEmpty()) throw new ProtocolError();
        var key=ClientTrust.key(pinnedJwk); current=Keys.thumbprint(key); keys.put(current,key); this.revision=ProtocolJson.integer(revision,1,ProtocolJson.MAX_INTEGER);
    }
    public synchronized Map<String,Object> lookup(String kid) { ProtocolJson.text(kid,true); if(!keys.containsKey(kid)) throw new ProtocolError(); return Map.copyOf(keys.get(kid)); }
    public synchronized void transition(Map<String,Object> newJwk,long revision,byte[] oldLeaseSignature) {
        var key=ClientTrust.key(newJwk); String fingerprint=Keys.thumbprint(key); ProtocolJson.integer(revision,1,ProtocolJson.MAX_INTEGER);
        if(revision<=this.revision || keys.containsKey(fingerprint)) throw new ProtocolError();
        byte[] signed=ProtocolJson.context(List.of("cerberus-lease-key-transition-v1",issuer,current,fingerprint,revision));
        if(!Keys.verify(keys.get(current),signed,oldLeaseSignature)) throw new ProtocolError();
        keys.put(fingerprint,key); current=fingerprint; this.revision=revision;
    }
}
