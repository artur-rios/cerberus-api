package cerberus.protocol;
import java.nio.charset.StandardCharsets;
import java.util.*;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;
import static cerberus.protocol.Fixtures.*;

class WireTest {
    private byte[] encode(Map<String,Object> value,int limit) {
        return (byte[])call("Wire","encode",new Class<?>[]{Map.class,int.class},value,limit);
    }
    @Test void GivenNestedProtocolObject_WhenEncoded_ThenStableStrictUtf8Bytes() {
        assertArrayEquals("{\"a\":[1,true,null,\"é\",\"\\u001b\"],\"z\":{\"x\":9007199254740991}}".getBytes(StandardCharsets.UTF_8),
            encode(Map.of("z",Map.of("x",9007199254740991L),"a",Arrays.asList(1L,true,null,"é","\u001b")),1048576));
    }
    @Test void GivenInvalidProtocolObject_WhenEncoded_ThenRedactedRejection() {
        rejected(()->encode(null,1048576));
        for(Object value:List.of(1.0,Double.NaN,9007199254740992L,"\ud800",new Object())) rejected(()->encode(Map.of("x",value),1048576));
        rejected(()->encode(Map.of("é",1L),1048576));
    }
    @Test void GivenAggregateLimit_WhenEncoded_ThenRejectBeforeOversizedResult() {
        assertArrayEquals("{\"x\":\"a\"}".getBytes(StandardCharsets.UTF_8),encode(Map.of("x","a"),9));
        rejected(()->encode(Map.of("x","a"),8)); rejected(()->encode(Map.of("x","x".repeat(1048576)),1048576));
    }
    @Test void GivenCyclicObject_WhenEncoded_ThenStableRejection() {
        Map<String,Object> cyclic=new LinkedHashMap<>(); cyclic.put("x",cyclic);
        rejected(()->encode(cyclic,1048576));
    }
}
