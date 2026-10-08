package cerberus.protocol;

import java.util.*;

/** Independently provisioned account and role pins, not directory TOFU. */
public final class ClientTrust {
    private final String accountId;
    private Map<String,Object> recipient,author;
    private long revision;
    record Pins(Map<String,Object> recipient,Map<String,Object> author) {}
    static Map<String,Object> key(Map<String,Object> jwk) {
        return Keys.validatePublic(Protection.snapshot(jwk,Set.of("crv","kty","x","y")));
    }
    public ClientTrust(String accountId,Map<String,Object> recipientJwk,Map<String,Object> authorJwk,long directoryRevision) {
        this.accountId=ProtocolJson.guid(accountId);
        recipient=key(recipientJwk); author=key(authorJwk); revision=ProtocolJson.integer(directoryRevision,1,ProtocolJson.MAX_INTEGER);
        if(Keys.thumbprint(recipient).equals(Keys.thumbprint(author))) throw new ProtocolError();
    }
    synchronized Pins pins(String expectedAccount) {
        if(!accountId.equals(ProtocolJson.guid(expectedAccount))) throw new ProtocolError();
        return new Pins(recipient,author);
    }
    public synchronized void transition(String accountId,String role,Map<String,Object> newJwk,long revision,byte[] oldAuthorSignature) {
        ProtocolJson.guid(accountId); ProtocolJson.integer(revision,1,ProtocolJson.MAX_INTEGER);
        if(!this.accountId.equals(accountId) || role==null || !Set.of("recipient-kem","envelope-author").contains(role) || revision<=this.revision) throw new ProtocolError();
        Map<String,Object> newKey=key(newJwk),old=role.equals("recipient-kem")?recipient:author,other=role.equals("recipient-kem")?author:recipient;
        String fingerprint=Keys.thumbprint(newKey);
        if(fingerprint.equals(Keys.thumbprint(old)) || fingerprint.equals(Keys.thumbprint(other))) throw new ProtocolError();
        byte[] signed=ProtocolJson.context(List.of("cerberus-key-transition-v1",accountId,role,Keys.thumbprint(old),fingerprint,revision));
        if(!Keys.verify(author,signed,oldAuthorSignature)) throw new ProtocolError();
        if(role.equals("recipient-kem")) recipient=newKey; else author=newKey;
        this.revision=revision;
    }
}
