package cerberus.protocol;

import java.util.*;

/** Immutable externally expected identity, never inferred from a ciphertext. */
public record Context(String ownerId,String resourceKind,String resourceId,long keyEpoch,List<Object> extraContext) {
    public Context {
        ProtocolJson.guid(ownerId); ProtocolJson.text(resourceKind,true); ProtocolJson.guid(resourceId);
        ProtocolJson.integer(keyEpoch,1,ProtocolJson.MAX_INTEGER);
        if(extraContext==null) throw new ProtocolError();
        try {
            extraContext=freeze(extraContext);
            if(ProtocolJson.context(extraContext).length>ProtocolJson.MAX_REQUEST) throw new ProtocolError();
        } catch(RuntimeException ex) { throw new ProtocolError(); }
    }
    private static List<Object> freeze(List<?> source) {
        List<Object> copy=new ArrayList<>();
        for(Object item:source) copy.add(item instanceof List<?> nested ? freeze(nested) : item);
        return Collections.unmodifiableList(copy);
    }
    List<Object> values() { return Arrays.asList(ownerId,resourceKind,resourceId,keyEpoch,extraContext); }
}
