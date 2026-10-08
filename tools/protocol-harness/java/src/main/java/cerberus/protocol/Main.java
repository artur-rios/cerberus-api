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
        else throw new ProtocolError();
    }
}
