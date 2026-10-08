package cerberus.protocol;

import java.lang.reflect.InvocationTargetException;
import java.nio.file.*;
import java.util.*;
import static org.junit.jupiter.api.Assertions.*;

/** Public data and test invocation only; no shared crypto implementation. */
final class Fixtures {
    @SuppressWarnings("unchecked") static Map<String,Object> map(Object value) { return (Map<String,Object>) value; }
    static Map<String,Object> document(String filename, Set<String> fields) {
        try { return ProtocolJson.parse(Files.readAllBytes(Path.of(System.getProperty("basedir"),"../fixtures",filename)), fields, Set.of(),1048576); }
        catch (Exception ex) { throw new AssertionError("public fixture unreadable",ex); }
    }
    static Map<String,Object> known(String name) {
        return map(document("known-answers.json",Set.of("schemaVersion","classification","hkdf","gcm","ecdsa","thumbprint","argon2","hpke")).get(name));
    }
    static Map<String,Object> fixture(String role) {
        return map(map(document("input.json",Set.of("schemaVersion","classification","keys")).get("keys")).get(role));
    }
    static byte[] privateDer(String role) { return Base64.getUrlDecoder().decode((String)fixture(role).get("privateDer")); }
    static Map<String,Object> publicJwk(String role) { return map(fixture(role).get("publicJwk")); }
    static byte[] hex(Map<String,Object> value, String key) { return HexFormat.of().parseHex((String)value.get(key)); }
    static Object call(String cls, String method, Class<?>[] types, Object... args) {
        Class<?> feature = assertDoesNotThrow(() -> Class.forName("cerberus.protocol."+cls),cls+" feature missing");
        try { return feature.getMethod(method,types).invoke(null,args); }
        catch (InvocationTargetException ex) {
            if (ex.getCause() instanceof RuntimeException runtime) throw runtime;
            throw new AssertionError(ex.getCause());
        } catch (ReflectiveOperationException ex) { throw new AssertionError(ex); }
    }
    static void rejected(Runnable action) {
        RuntimeException error = assertThrows(RuntimeException.class,action::run);
        assertEquals("invalid_protocol",error.getMessage());
    }
}
