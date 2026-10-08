package cerberus.protocol;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.util.*;
import com.fasterxml.jackson.core.*;
import com.fasterxml.jackson.core.json.JsonWriteFeature;

/** Deterministic bounded protocol-object output, not arbitrary JCS. */
public final class Wire {
    private Wire() {}
    private static final JsonFactory FACTORY=JsonFactory.builder().disable(JsonWriteFeature.WRITE_HEX_UPPER_CASE).build();
    private static final class BoundedOutput extends ByteArrayOutputStream {
        private final int limit;
        BoundedOutput(int limit) { this.limit=limit; }
        @Override public synchronized void write(int value) {
            if(count>=limit) throw new ProtocolError(); super.write(value);
        }
        @Override public synchronized void write(byte[] value,int offset,int length) {
            if((long)count+length>limit) throw new ProtocolError(); super.write(value,offset,length);
        }
    }
    private static void write(JsonGenerator generator,Object value) throws IOException {
        if(value==null) generator.writeNull();
        else if(value instanceof Boolean bool) generator.writeBoolean(bool);
        else if(value instanceof String string) generator.writeString(ProtocolJson.text(string,false));
        else if(value instanceof Long || value instanceof Integer) generator.writeNumber(ProtocolJson.integer(value,-ProtocolJson.MAX_INTEGER,ProtocolJson.MAX_INTEGER));
        else if(value instanceof List<?> list) {
            generator.writeStartArray(); for(Object item:list) write(generator,item); generator.writeEndArray();
        } else if(value instanceof Map<?,?> map) {
            SortedMap<String,Object> fields=new TreeMap<>();
            for(var entry:map.entrySet()) fields.put(ProtocolJson.text(entry.getKey(),true),entry.getValue());
            generator.writeStartObject(); for(var entry:fields.entrySet()) { generator.writeFieldName(entry.getKey()); write(generator,entry.getValue()); } generator.writeEndObject();
        } else throw new ProtocolError();
    }
    public static byte[] encode(Map<String,Object> value) { return encode(value,ProtocolJson.MAX_REQUEST); }
    public static byte[] encode(Map<String,Object> value,int maxBytes) {
        if(value==null || maxBytes<1 || maxBytes>ProtocolJson.MAX_REQUEST) throw new ProtocolError();
        try {
            BoundedOutput output=new BoundedOutput(maxBytes);
            try(var generator=FACTORY.createGenerator(output)) { write(generator,value); }
            return output.toByteArray();
        } catch(Exception ex) { throw new ProtocolError(); }
    }
}
