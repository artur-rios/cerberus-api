package cerberus.protocol;

import java.util.Arrays;
import org.bouncycastle.crypto.digests.SHA256Digest;
import org.bouncycastle.crypto.engines.AESEngine;
import org.bouncycastle.crypto.generators.Argon2BytesGenerator;
import org.bouncycastle.crypto.generators.HKDFBytesGenerator;
import org.bouncycastle.crypto.modes.GCMBlockCipher;
import org.bouncycastle.crypto.params.*;

/** Maintained library primitives with fixed protocol settings and no fallback. */
public final class Primitives {
    private Primitives() {}
    private static void bytes(byte[]... values) {
        for(byte[] value:values) if(value==null) throw new ProtocolError();
    }
    public static byte[] hkdf(byte[] root,byte[] salt,byte[] info,int length) {
        bytes(root,salt,info);
        if(root.length==0 || length<1 || length>8160) throw new ProtocolError();
        try {
            HKDFBytesGenerator generator=new HKDFBytesGenerator(new SHA256Digest());
            generator.init(new HKDFParameters(root,salt,info));
            byte[] output=new byte[length]; generator.generateBytes(output,0,length); return output;
        } catch(Exception ex) { throw new ProtocolError(); }
    }
    public static byte[] argon2(byte[] password,byte[] salt) {
        bytes(password,salt);
        if(salt.length!=16) throw new ProtocolError();
        try {
            Argon2BytesGenerator generator=new Argon2BytesGenerator();
            generator.init(new Argon2Parameters.Builder(Argon2Parameters.ARGON2_id).withVersion(Argon2Parameters.ARGON2_VERSION_13)
                .withMemoryAsKB(65536).withIterations(3).withParallelism(4).withSalt(salt).build());
            byte[] output=new byte[32]; generator.generateBytes(password,output); return output;
        } catch(Exception ex) { throw new ProtocolError(); }
    }
    private static byte[] gcm(boolean encrypt,byte[] key,byte[] nonce,byte[] aad,byte[] value) {
        bytes(key,nonce,aad,value);
        if(key.length!=32 || nonce.length!=12 || (!encrypt && value.length<16)) throw new ProtocolError();
        byte[] output=null;
        try {
            var cipher=GCMBlockCipher.newInstance(AESEngine.newInstance());
            cipher.init(encrypt,new AEADParameters(new KeyParameter(key),128,nonce,aad));
            output=new byte[cipher.getOutputSize(value.length)];
            int count=cipher.processBytes(value,0,value.length,output,0);
            count+=cipher.doFinal(output,count);
            // Authentication succeeds before any decrypted buffer leaves this method.
            return Arrays.copyOf(output,count);
        } catch(Exception ex) { throw new ProtocolError(); }
        finally { if(output!=null) Arrays.fill(output,(byte)0); }
    }
    public static byte[] gcmSeal(byte[] key,byte[] nonce,byte[] aad,byte[] plaintext) { return gcm(true,key,nonce,aad,plaintext); }
    public static byte[] gcmOpen(byte[] key,byte[] nonce,byte[] aad,byte[] ciphertextAndTag) { return gcm(false,key,nonce,aad,ciphertextAndTag); }
    public static byte[] sha256(byte[] value) {
        bytes(value); SHA256Digest digest=new SHA256Digest(); digest.update(value,0,value.length);
        byte[] result=new byte[32]; digest.doFinal(result,0); return result;
    }
}
