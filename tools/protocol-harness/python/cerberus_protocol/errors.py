"""Stable, deliberately secret-free failures."""


class ProtocolError(ValueError):
    def __init__(self, code: str = "invalid_protocol"):
        if code not in ("invalid_protocol", "unsupported_dependency"):
            code = "invalid_protocol"
        self.code = code
        super().__init__(code)
