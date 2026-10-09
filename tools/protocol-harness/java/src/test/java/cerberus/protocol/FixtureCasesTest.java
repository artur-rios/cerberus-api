package cerberus.protocol;

import java.util.*;
import java.nio.file.*;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

public class FixtureCasesTest {
    public static class Probe {
        public static void main(String[] args) {
            var context=new Context(FixtureCases.ACCOUNT,"account",FixtureCases.ACCOUNT,1,List.of());
            var output=Symmetric.seal(FixtureCases.ROOT,context,"cerberus-content-v1",new byte[]{1},new NonceGuard(0));
            if(!Arrays.equals(new byte[]{1},Symmetric.open(FixtureCases.ROOT,context,"cerberus-content-v1",output))) System.exit(3);
            var test=new LinkedHashMap<String,Object>(); test.put("id","content.account.mutate.tag"); test.put("output",output);
            try { FixtureCases.execute(test); }
            catch(AssertionError expected) { System.exit(0); }
            catch(ProtocolError wrongFailure) { System.exit(2); }
            System.exit(1);
        }
    }
    @Test void GivenValidNegativeEnvelopeWithDifferentPlaintext_WhenExecuted_ThenFixtureFailureIsNotProtocolRejection() throws Exception {
        var root=Path.of(System.getProperty("basedir")).getParent().getParent().getParent();
        var child=new ProcessBuilder(Path.of(System.getProperty("java.home"),"bin/java").toString(),"-cp",System.getProperty("java.class.path"),Probe.class.getName()).directory(root.toFile()).redirectErrorStream(true).start();
        try { assertTrue(child.waitFor(10,java.util.concurrent.TimeUnit.SECONDS)); assertEquals(0,child.exitValue()); }
        finally { child.destroyForcibly(); }
    }
}
