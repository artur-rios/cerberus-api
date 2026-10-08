package cerberus.protocol;

import java.util.*;

/** Local reference model only; production cross-device accounting is separate. */
public final class NonceGuard {
    private final long initialCount;
    private boolean seeded;
    private final Map<String,Long> counts=new HashMap<>();
    private final Set<String> used=new HashSet<>();
    public NonceGuard(long initialCount) {
        if(initialCount<0 || initialCount>(1L<<32)) throw new ProtocolError();
        this.initialCount=initialCount;
    }
    public synchronized void reserve(byte[] root,Context expected,byte[] salt) {
        if(root==null || root.length!=32 || salt==null || salt.length!=32 || expected==null) throw new ProtocolError();
        byte[] snapshot=root.clone(), saltSnapshot=salt.clone(), encoded=ProtocolJson.context(expected.values());
        byte[] identity=new byte[32+encoded.length]; System.arraycopy(snapshot,0,identity,0,32); System.arraycopy(encoded,0,identity,32,encoded.length);
        String fingerprint=ProtocolJson.base64(Primitives.sha256(identity));
        String pair=fingerprint+":"+ProtocolJson.base64(saltSnapshot);
        if(!counts.containsKey(fingerprint)) { counts.put(fingerprint,seeded?0L:initialCount); seeded=true; }
        long count=counts.get(fingerprint);
        if(count>=(1L<<32) || used.contains(pair)) throw new ProtocolError();
        used.add(pair); counts.put(fingerprint,count+1);
    }
}
