package cerberus.protocol;

import com.fasterxml.jackson.core.*;
import com.fasterxml.jackson.core.json.JsonReadFeature;
import com.fasterxml.jackson.core.json.JsonWriteFeature;
import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.nio.charset.*;
import java.util.*;

/** Java-owned encoding rules. Nothing delegates to the Python implementation. */
public final class ProtocolJson {
    public static final long MAX_INTEGER = 9007199254740991L;
    public static final int MAX_REQUEST = 1048576;
    private static final JsonFactory JSON = JsonFactory.builder()
        .enable(StreamReadFeature.STRICT_DUPLICATE_DETECTION)
        .disable(JsonReadFeature.ALLOW_NON_NUMERIC_NUMBERS)
        .disable(JsonWriteFeature.WRITE_HEX_UPPER_CASE).build();
    private ProtocolJson() { }

    public static long integer(Object value, long minimum, long maximum) {
        if (!(value instanceof Long || value instanceof Integer)) throw new ProtocolError();
        long number = ((Number)value).longValue();
        if (number < minimum || number > maximum) throw new ProtocolError();
        return number;
    }
    public static String text(Object value, boolean asciiOnly) {
        if (!(value instanceof String s)) throw new ProtocolError();
        for (int i=0; i<s.length(); i++) {
            char c=s.charAt(i);
            if (asciiOnly && c>127) throw new ProtocolError();
            if (Character.isHighSurrogate(c)) {
                if (++i >= s.length() || !Character.isLowSurrogate(s.charAt(i))) throw new ProtocolError();
            } else if (Character.isLowSurrogate(c)) throw new ProtocolError();
        }
        return s;
    }
    public static byte[] utf8(String value) { return text(value,false).getBytes(StandardCharsets.UTF_8); }
    public static byte[] context(List<Object> values) {
        if (values == null) throw new ProtocolError();
        try {
            ByteArrayOutputStream output = new ByteArrayOutputStream();
            try (JsonGenerator writer=JSON.createGenerator(output)) { writeContext(writer,values); }
            return output.toByteArray();
        } catch (Exception | StackOverflowError ex) { throw new ProtocolError(); }
    }
    private static void writeContext(JsonGenerator writer,Object value) throws java.io.IOException {
        if (value==null) writer.writeNull();
        else if (value instanceof Boolean b) writer.writeBoolean(b);
        else if (value instanceof String s) writer.writeString(text(s,true));
        else if (value instanceof Long || value instanceof Integer) writer.writeNumber(integer(value,-MAX_INTEGER,MAX_INTEGER));
        else if (value instanceof List<?> items) {
            writer.writeStartArray(); for (Object item:items) writeContext(writer,item); writer.writeEndArray();
        } else throw new ProtocolError();
    }
    @SuppressWarnings("unchecked")
    public static Map<String,Object> fields(Object value,Set<String> required,Set<String> optional) {
        if (!(value instanceof Map<?,?> map) || !map.keySet().containsAll(required)) throw new ProtocolError();
        for (Object key:map.keySet()) if (!(key instanceof String) || !(required.contains(key) || optional.contains(key))) throw new ProtocolError();
        return (Map<String,Object>)map;
    }
    public static Map<String,Object> parse(byte[] raw,Set<String> required,Set<String> optional,int maxBytes) {
        return fields(object(raw,maxBytes),required,optional);
    }
    /** Strict syntax only; owning contracts still validate exact field sets. */
    public static Map<String,Object> object(byte[] raw,int maxBytes) {
        if (raw==null || maxBytes<1 || raw.length>maxBytes) throw new ProtocolError();
        try {
            String decoded=StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(raw)).toString();
            if (decoded.startsWith("\ufeff")) throw new ProtocolError();
            try (JsonParser parser=JSON.createParser(decoded)) {
                if (parser.nextToken()==null) throw new ProtocolError();
                Object result=read(parser);
                if (parser.nextToken()!=null) throw new ProtocolError();
                if(!(result instanceof Map<?,?> map)) throw new ProtocolError();
                return fields(map,map.keySet().stream().map(key->text(key,false)).collect(java.util.stream.Collectors.toSet()),Set.of());
            }
        } catch (Exception | StackOverflowError ex) { throw new ProtocolError(); }
    }
    private static Object read(JsonParser parser) throws java.io.IOException {
        return switch(parser.currentToken()) {
            case START_OBJECT -> {
                Map<String,Object> result=new LinkedHashMap<>();
                while(parser.nextToken()!=JsonToken.END_OBJECT) {
                    if(parser.currentToken()!=JsonToken.FIELD_NAME) throw new ProtocolError();
                    String key=text(parser.currentName(),false);
                    if(result.containsKey(key) || parser.nextToken()==null) throw new ProtocolError();
                    result.put(key,read(parser));
                }
                yield result;
            }
            case START_ARRAY -> {
                List<Object> result=new ArrayList<>();
                while(parser.nextToken()!=JsonToken.END_ARRAY) {
                    if(parser.currentToken()==null) throw new ProtocolError();
                    result.add(read(parser));
                }
                yield result;
            }
            case VALUE_STRING -> text(parser.getText(),false);
            case VALUE_NUMBER_INT -> integer(parser.getLongValue(),-MAX_INTEGER,MAX_INTEGER);
            case VALUE_TRUE -> true;
            case VALUE_FALSE -> false;
            case VALUE_NULL -> null;
            default -> throw new ProtocolError();
        };
    }
    public static String guid(String value) {
        if(value==null || !value.matches("[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}") || value.equals("00000000-0000-0000-0000-000000000000")) throw new ProtocolError();
        return value;
    }
    public static String base64(byte[] value) {
        if(value==null) throw new ProtocolError();
        return Base64.getUrlEncoder().withoutPadding().encodeToString(value);
    }
    public static byte[] unbase64(String value,int expectedLength) {
        if(value==null || expectedLength<0 || value.length()!=((long)expectedLength*8+5)/6 || !value.matches("[A-Za-z0-9_-]*")) throw new ProtocolError();
        try {
            byte[] decoded=Base64.getUrlDecoder().decode(value);
            if(decoded.length!=expectedLength || !base64(decoded).equals(value)) throw new ProtocolError();
            return decoded;
        } catch(IllegalArgumentException ex) { throw new ProtocolError(); }
    }
}
