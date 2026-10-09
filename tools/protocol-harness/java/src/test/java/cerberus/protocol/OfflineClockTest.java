package cerberus.protocol;

import java.util.*;
import java.util.concurrent.atomic.AtomicLong;
import java.util.function.LongSupplier;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;
import static cerberus.protocol.Fixtures.*;
import static cerberus.protocol.LeaseTest.*;

class OfflineClockTest {
    AtomicLong wall=new AtomicLong(NOW),monotonic=new AtomicLong(100);
    Object clock; Map<String,Object> lease;
    @BeforeEach void setup() {
        clock=create(); lease=claims(expected(true),NOW,true,null);
    }
    Object create() { return assertDoesNotThrow(()->Class.forName("cerberus.protocol.OfflineClock").getConstructor(LongSupplier.class,LongSupplier.class).newInstance((LongSupplier)wall::get,(LongSupplier)monotonic::get),"OfflineClock missing"); }
    void renew(Object c,Map<String,Object> value,boolean online) { invoke(c,"renew",new Class<?>[]{Map.class,boolean.class},value,online); }
    long time(Object c) { return (Long)invoke(c,"effectiveNow",new Class<?>[]{}); }
    void restart(Object c) { invoke(c,"restart",new Class<?>[]{}); }
    @Test void GivenCachedLease_WhenClockRestarts_ThenOnlineRenewalRequired() {
        renew(clock,lease,true); assertEquals(NOW,time(clock)); wall.addAndGet(10); monotonic.addAndGet(10); assertEquals(NOW+10,time(clock)); restart(clock);
        verify(sign(lease),expected(true),createTrust(),NOW+10); rejected(()->time(clock));
    }
    @Test void GivenWallOrMonotonicRegression_WhenObserved_ThenRenewalRequired() {
        for(boolean regressWall:List.of(true,false)) {
            wall.set(NOW); monotonic.set(100); Object c=create(); renew(c,lease,true); wall.addAndGet(10); monotonic.addAndGet(10); time(c);
            if(regressWall) wall.decrementAndGet(); else monotonic.decrementAndGet(); rejected(()->time(c)); wall.addAndGet(100); monotonic.addAndGet(100); rejected(()->time(c));
        }
    }
    @Test void GivenCachedImportOrUnauthenticatedRenewal_WhenUsed_ThenAnchorCannotReset() { renew(clock,lease,true); wall.addAndGet(10); monotonic.addAndGet(10); assertEquals(NOW+10,time(clock)); rejected(()->renew(clock,lease,false)); assertEquals(NOW+10,time(clock)); }
    @Test void GivenServerTimeAndHighWater_WhenRenewed_ThenTimeNeverMovesBack() {
        wall.set(NOW-100); renew(clock,lease,true); monotonic.addAndGet(10); assertEquals(NOW+10,time(clock)); restart(clock); renew(clock,claims(expected(true),NOW+5,true,null),true); assertEquals(NOW+10,time(clock));
    }
    @Test void GivenDisabledExpiry_WhenVerified_ThenNoPeriodicClockAnchorRequired() { var disabled=claims(expected(false),NOW,false,null); assertEquals(disabled,verify(sign(disabled),expected(false),createTrust(),NOW+1000000000)); rejected(()->time(clock)); }
    @Test void GivenInvalidTimeOrOverflow_WhenObserved_ThenRejected() { renew(clock,lease,true); wall.set(-1); rejected(()->time(clock)); wall.set(NOW); renew(clock,lease,true); monotonic.set(ProtocolJson.MAX_INTEGER); rejected(()->time(clock)); }
}
