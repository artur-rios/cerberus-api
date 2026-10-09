package cerberus.protocol;

import java.util.*;
import java.util.function.LongSupplier;

/** Process-local enabled-expiry model; renew is an explicit trusted online event. */
public final class OfflineClock {
    private final LongSupplier wall,monotonic;
    private boolean anchored;
    private long highest,server,receipt,lastWall,lastMonotonic;
    public OfflineClock(LongSupplier wall,LongSupplier monotonic) { if(wall==null || monotonic==null) throw new ProtocolError(); this.wall=wall; this.monotonic=monotonic; }
    private static long seconds(long value) { return ProtocolJson.integer(value,0,ProtocolJson.MAX_INTEGER); }
    public synchronized void renew(Map<String,Object> freshlyVerifiedLease,boolean authenticatedOnline) {
        if(!authenticatedOnline) throw new ProtocolError(); var value=Lease.validate(freshlyVerifiedLease); if(!Boolean.TRUE.equals(value.get("renewalEnabled"))) throw new ProtocolError();
        long w=seconds(wall.getAsLong()),m=seconds(monotonic.getAsLong()); server=(Long)value.get("iat"); receipt=m; lastWall=w; lastMonotonic=m; highest=Math.max(highest,Math.max(w,server)); anchored=true;
    }
    public synchronized long effectiveNow() {
        if(!anchored) throw new ProtocolError();
        try {
            long w=seconds(wall.getAsLong()),m=seconds(monotonic.getAsLong()); if(w<lastWall || m<lastMonotonic) throw new ProtocolError();
            long projected=seconds(server+m-receipt); highest=Math.max(highest,Math.max(w,projected)); lastWall=w; lastMonotonic=m; return highest;
        } catch(ProtocolError ex) { anchored=false; throw ex; }
    }
    public synchronized void restart() { anchored=false; }
}
