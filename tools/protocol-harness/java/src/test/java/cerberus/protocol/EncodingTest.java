package cerberus.protocol;

import java.lang.reflect.InvocationTargetException;
import java.nio.charset.StandardCharsets;
import java.util.*;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;
import static org.junit.jupiter.api.Assertions.*;

/** Independent literal expectations; reflection makes RED fail on the missing feature rather than uncompilable tests. */
class EncodingTest {
    private Object call(String method, Class<?>[] types, Object... args) {
        Class<?> cls = assertDoesNotThrow(() -> Class.forName("cerberus.protocol.ProtocolJson"), "encoding feature missing");
        try { return cls.getMethod(method, types).invoke(null, args); }
        catch (InvocationTargetException ex) {
            if (ex.getCause() instanceof RuntimeException runtime) throw runtime;
            throw new AssertionError(ex.getCause());
        } catch (ReflectiveOperationException ex) { throw new AssertionError(ex); }
    }
    private Object parse(byte[] raw, Set<String> required, int limit) {
        return call("parse", new Class<?>[]{byte[].class, Set.class, Set.class, int.class}, raw, required, Set.of(), limit);
    }
    private void rejected(Runnable action) {
        RuntimeException failure = assertThrows(RuntimeException.class, action::run);
        assertEquals("invalid_protocol", failure.getMessage());
    }
    @Test void GivenCanonicalArray_WhenEncoded_ThenBytesMatch() {
        assertArrayEquals("[\"cerberus-v1\",1,[],true,null]".getBytes(StandardCharsets.UTF_8),
            (byte[])call("context", new Class<?>[]{List.class}, Arrays.asList("cerberus-v1", 1L, List.of(), true, null)));
    }
    @Test void GivenEscapes_WhenEncoded_ThenMinimalJson() {
        assertArrayEquals("[\"/\",\"\\\"\",\"\\\\\",\"\\n\"]".getBytes(StandardCharsets.UTF_8),
            (byte[])call("context", new Class<?>[]{List.class}, List.of("/", "\"", "\\", "\n")));
    }
    @Test void GivenInvalidArrayValues_WhenEncoded_ThenReject() {
        rejected(() -> call("context", new Class<?>[]{List.class}, List.of("é")));
        rejected(() -> call("context", new Class<?>[]{List.class}, List.of(Map.of())));
        rejected(() -> call("context", new Class<?>[]{List.class}, List.of(1.0)));
        rejected(() -> call("context", new Class<?>[]{List.class}, List.of(9007199254740992L)));
    }
    @Test void GivenControlEscape_WhenEncoded_ThenLowercaseCanonicalBytes() {
        assertArrayEquals("[\"\\u001b\"]".getBytes(StandardCharsets.UTF_8),
            (byte[])call("context",new Class<?>[]{List.class},List.of("\u001b")));
    }
    @Test void GivenIntegerBoundaries_WhenDecoded_ThenAccept() {
        assertEquals(1L, call("integer", new Class<?>[]{Object.class,long.class,long.class}, 1L,1L,9007199254740991L));
        assertEquals(9007199254740991L, call("integer", new Class<?>[]{Object.class,long.class,long.class}, 9007199254740991L,1L,9007199254740991L));
        assertEquals(0L, call("integer", new Class<?>[]{Object.class,long.class,long.class}, 0L,0L,9007199254740991L));
    }
    @Test void GivenInvalidInteger_WhenDecoded_ThenReject() {
        for (Object value : Arrays.asList(true,false,0L,-1L,9007199254740992L,"1",1.0,null))
            rejected(() -> call("integer", new Class<?>[]{Object.class,long.class,long.class}, value,1L,9007199254740991L));
    }
    @Test void GivenReorderedProperties_WhenParsed_ThenSameMeaning() {
        assertEquals(Map.of("x",1L,"y",2L), parse("{\"y\":2,\"x\":1}".getBytes(StandardCharsets.UTF_8), Set.of("x","y"),1024));
    }
    @ParameterizedTest(name="GivenInvalidJson{index}_WhenParsed_ThenReject")
    @ValueSource(strings={"{\"x\":1,\"x\":2}","{\"x\":{\"y\":1,\"y\":2}}","{\"x\":1e0}","{\"x\":1.0}",
        "{\"x\":9007199254740992}","{\"x\":NaN}","{\"x\":Infinity}","{\"x\":1}{}","[]","{}", "{\"x\":1,\"unknown\":2}","{\"x\":1} trailing","{\"x\":}"})
    void GivenInvalidJson_WhenParsed_ThenReject(String raw) { rejected(() -> parse(raw.getBytes(StandardCharsets.UTF_8),Set.of("x"),1048576)); }
    @ParameterizedTest @ValueSource(strings={"\ufeff{\"x\":1}","{\"x\":\"\\ud800\"}","{\"\\udfff\":1}"})
    void GivenInvalidUnicode_WhenParsed_ThenReject(String raw) { rejected(() -> parse(raw.getBytes(StandardCharsets.UTF_8),Set.of("x"),1024)); }
    @Test void GivenInvalidUtf8_WhenParsed_ThenReject() { rejected(() -> parse(new byte[]{'{','"','x','"',':','"',(byte)255,'"','}'},Set.of("x"),1024)); }
    @Test void GivenValidUnicodePair_WhenParsed_ThenAccept() {
        assertEquals(Map.of("x","😀"),parse("{\"x\":\"\\ud83d\\ude00\"}".getBytes(StandardCharsets.UTF_8),Set.of("x"),1024));
    }
    @Test void GivenAggregateLimit_WhenParsed_ThenBoundWholeRequest() {
        assertEquals(Map.of("x",1L),parse("{\"x\":1}".getBytes(StandardCharsets.UTF_8),Set.of("x"),7));
        rejected(() -> parse("{\"x\":1} ".getBytes(StandardCharsets.UTF_8),Set.of("x"),7));
        rejected(() -> parse("{\"x\":[1,2,3,4]}".getBytes(StandardCharsets.UTF_8),Set.of("x"),7));
    }
    @Test void GivenCanonicalGuid_WhenValidated_ThenPreserve() {
        assertEquals("00000000-0000-0000-0000-000000000001",call("guid",new Class<?>[]{String.class},"00000000-0000-0000-0000-000000000001"));
    }
    @ParameterizedTest @ValueSource(strings={"","00000000-0000-0000-0000-000000000000","AAAAAAAA-0000-0000-0000-000000000001","00000000000000000000000000000001","{00000000-0000-0000-0000-000000000001}"})
    void GivenInvalidGuid_WhenValidated_ThenReject(String raw) { rejected(() -> call("guid",new Class<?>[]{String.class},raw)); }
    @Test void GivenCanonicalBase64_WhenDecoded_ThenBytesMatch() {
        assertEquals("_wA",call("base64",new Class<?>[]{byte[].class}, new byte[]{(byte)255,0}));
        assertArrayEquals(new byte[]{(byte)255,0},(byte[])call("unbase64",new Class<?>[]{String.class,int.class},"_wA",2));
    }
    @ParameterizedTest @ValueSource(strings={"","_wA=","_wA\n","/wA","_wB","A","AA"})
    void GivenInvalidBase64_WhenDecoded_ThenReject(String raw) { rejected(() -> call("unbase64",new Class<?>[]{String.class,int.class},raw,2)); }
}
