package cerberus.protocol;

import java.io.*;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class DiagnosticsTest {
    @Test void GivenSecretBearingException_WhenHandled_ThenOnlyStableCodeEmitted() throws Exception {
        Class<?> cli=assertDoesNotThrow(()->Class.forName("cerberus.protocol.Main"),"CLI missing"); var bytes=new ByteArrayOutputStream(); var previous=System.err;
        try {
            System.setErr(new PrintStream(bytes)); Object result=cli.getMethod("safe",Runnable.class).invoke(null,(Runnable)()->{ throw new IllegalArgumentException("marker-secret plaintext private proof"); });
            assertEquals(1,result); assertTrue(bytes.toString().contains("invalid_protocol")); assertFalse(bytes.toString().contains("marker-secret"));
        } finally { System.setErr(previous); }
    }
}
