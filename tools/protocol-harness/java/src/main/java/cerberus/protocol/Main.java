package cerberus.protocol;

import java.nio.file.*;
import java.util.*;

/** Explicit public-fixture commands only. No raw exception diagnostics. */
public final class Main {
    private Main() {}
    public static int safe(Runnable action) {
        try { action.run(); return 0; }
        catch(Throwable ex) { String code=ex instanceof LinkageError || ex instanceof ProtocolError p && p.code.equals("unsupported_dependency")?"unsupported_dependency":"invalid_protocol"; System.err.println("{\"code\":\""+code+"\"}"); return 1; }
    }
    public static void main(String[] args) { System.exit(safe(()->run(args))); }
    private static void run(String[] args) {
        if(args.length==3 && args[0].equals("produce")) FixtureCases.write(Path.of(args[2]),FixtureCases.produce(FixtureCases.read(Path.of(args[1]))));
        else if(args.length==3 && args[0].equals("consume")) FixtureCases.write(Path.of(args[2]),FixtureCases.consume(FixtureCases.read(Path.of(args[1]))));
        else if(args.length==2 && args[0].equals("self-test")) FixtureCases.write(Path.of(args[1]),FixtureCases.selfTest());
        else if(args.length==3 && args[0].equals("benchmark")) benchmark(Integer.parseInt(args[1]),Path.of(args[2]));
        else throw new ProtocolError();
    }
    private static void benchmark(int samples,Path output) {
        if(samples<1 || samples>1000) throw new ProtocolError(); byte[] password="public benchmark fixture".getBytes(java.nio.charset.StandardCharsets.UTF_8),salt=new byte[16]; for(int i=0;i<16;i++) salt[i]=(byte)i;
        Primitives.argon2(password,salt); List<Double> times=new ArrayList<>(); for(int i=0;i<samples;i++) { long start=System.nanoTime(); Primitives.argon2(password,salt); times.add((System.nanoTime()-start)/1000000.0); }
        var environment=Map.of("os",System.getProperty("os.name")+" "+System.getProperty("os.version"),"architecture",System.getProperty("os.arch"),"runtime",System.getProperty("java.vendor")+" "+System.getProperty("java.version"),"library","Bouncy Castle "+new org.bouncycastle.jce.provider.BouncyCastleProvider().getVersionStr());
        // FixtureCases' protocol serializer intentionally rejects noninteger wire fields.
        try(var stream=java.nio.file.Files.newOutputStream(output);var json=new com.fasterxml.jackson.core.JsonFactory().createGenerator(stream)) {
            json.writeStartObject(); json.writeStringField("implementation","java"); json.writeStringField("version","1.0.0"); json.writeNumberField("samples",samples); json.writeArrayFieldStart("elapsedMilliseconds"); for(double time:times) json.writeNumber(time); json.writeEndArray();
            json.writeObjectFieldStart("profile"); json.writeNumberField("memoryKiB",65536); json.writeNumberField("iterations",3); json.writeNumberField("parallelism",4); json.writeNumberField("length",32); json.writeEndObject(); json.writeObjectFieldStart("environment"); for(var e:environment.entrySet()) json.writeStringField(e.getKey(),e.getValue()); json.writeEndObject(); json.writeEndObject();
        } catch(java.io.IOException ex) { throw new ProtocolError(); }
    }
}
