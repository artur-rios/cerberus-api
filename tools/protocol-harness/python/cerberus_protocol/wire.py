"""Bounded object output for protocol schemas; not a general JCS canonicalizer."""
import json
from .encoding import integer, text, MAX_INTEGER, MAX_REQUEST
from .errors import ProtocolError


def _validate(value):
    if value is None or type(value) is bool:
        return
    if type(value) is str:
        text(value)
    elif type(value) is int:
        integer(value, -MAX_INTEGER, MAX_INTEGER)
    elif type(value) is list:
        for item in value:
            _validate(item)
    elif type(value) is dict:
        for key, item in value.items():
            text(key, True)
            _validate(item)
    else:
        raise ProtocolError()


def encode(value: dict, max_bytes: int = MAX_REQUEST) -> bytes:
    if type(value) is not dict or type(max_bytes) is not int or not 1 <= max_bytes <= MAX_REQUEST:
        raise ProtocolError()
    try:
        _validate(value)
        result = bytearray()
        encoder = json.JSONEncoder(ensure_ascii=False, separators=(",", ":"), sort_keys=True, allow_nan=False)
        for part in encoder.iterencode(value):
            chunk = part.encode("utf-8", errors="strict")
            if len(result) + len(chunk) > max_bytes:
                raise ProtocolError()
            result.extend(chunk)
        return bytes(result)
    except (TypeError, ValueError, UnicodeError, RecursionError):
        raise ProtocolError() from None
