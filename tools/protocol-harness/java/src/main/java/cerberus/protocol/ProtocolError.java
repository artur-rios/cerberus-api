package cerberus.protocol;

/** Deliberately carries neither inputs nor provider exception details. */
public final class ProtocolError extends IllegalArgumentException {
    public final String code;
    public ProtocolError() { this("invalid_protocol"); }
    public ProtocolError(String code) {
        super("unsupported_dependency".equals(code) ? code : "invalid_protocol");
        this.code = getMessage();
    }
}
