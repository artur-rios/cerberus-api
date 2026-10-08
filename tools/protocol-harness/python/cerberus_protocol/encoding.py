"""Independent strict wire parser and constrained authenticated-array encoder."""
import base64 as _base64
import binascii
import json
import re
from .errors import ProtocolError

MAX_INTEGER = 9007199254740991
MAX_REQUEST = 1048576


def integer(value: object, minimum: int = 1, maximum: int = MAX_INTEGER) -> int:
    if type(value) is not int or not minimum <= value <= maximum:
        raise ProtocolError()
    return value


def text(value: object, ascii_only: bool = False) -> str:
    if type(value) is not str:
        raise ProtocolError()
    try:
        value.encode("ascii" if ascii_only else "utf-8", errors="strict")
    except UnicodeError:
        raise ProtocolError() from None
    return value


def utf8(value: object) -> bytes:
    return text(value).encode("utf-8", errors="strict")


def context(values: list[object]) -> bytes:
    def validate(value):
        if value is None or type(value) is bool:
            return
        if type(value) is str:
            text(value, True)
        elif type(value) is int:
            integer(value, -MAX_INTEGER, MAX_INTEGER)
        elif type(value) is list:
            for item in value: validate(item)
        else:
            raise ProtocolError()
    if type(values) is not list: raise ProtocolError()
    try:
        validate(values)
        return json.dumps(values, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode("ascii")
    except (RecursionError, ValueError, TypeError):
        raise ProtocolError() from None


def fields(value: object, required: set[str], optional: set[str] | None = None) -> dict:
    if type(value) is not dict or not required <= value.keys() or value.keys() - required - (optional or set()):
        raise ProtocolError()
    return value


def _pairs(pairs):
    result = {}
    for key, value in pairs:
        text(key)
        if key in result: raise ProtocolError()
        result[key] = value
    return result


def _invalid_number(_):
    raise ProtocolError()


def _parsed_integer(token):
    try:
        return integer(int(token), -MAX_INTEGER, MAX_INTEGER)
    except ValueError:
        raise ProtocolError() from None


def _unicode_tree(value):
    if type(value) is str: text(value)
    elif type(value) is dict:
        for key, item in value.items(): text(key); _unicode_tree(item)
    elif type(value) is list:
        for item in value: _unicode_tree(item)


def parse(raw: bytes, required: set[str], optional: set[str], max_bytes: int = MAX_REQUEST) -> dict:
    return fields(parse_object(raw, max_bytes), required, optional)


def parse_object(raw: bytes, max_bytes: int = MAX_REQUEST) -> dict:
    """Strict syntax only. Owning contracts must still validate exact field sets."""
    if type(raw) is not bytes or type(max_bytes) is not int or max_bytes < 1 or len(raw) > max_bytes or raw.startswith(b'\xef\xbb\xbf'):
        raise ProtocolError()
    try:
        result = json.loads(raw.decode("utf-8", errors="strict"), object_pairs_hook=_pairs,
                            parse_int=_parsed_integer, parse_float=_invalid_number, parse_constant=_invalid_number)
        _unicode_tree(result)
        if type(result) is not dict:
            raise ProtocolError()
        return result
    except (ValueError, UnicodeError, RecursionError, TypeError):
        raise ProtocolError() from None


def guid(value: object) -> str:
    if type(value) is not str or not re.fullmatch(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", value) or value == "00000000-0000-0000-0000-000000000000":
        raise ProtocolError()
    return value


def base64(value: bytes) -> str:
    if type(value) is not bytes: raise ProtocolError()
    return _base64.urlsafe_b64encode(value).decode("ascii").rstrip("=")


def unbase64(value: object, expected_length: int) -> bytes:
    if type(value) is not str or type(expected_length) is not int or expected_length < 0 or len(value) != (expected_length * 8 + 5) // 6 or not re.fullmatch(r"[A-Za-z0-9_-]*", value):
        raise ProtocolError()
    try:
        decoded = _base64.b64decode(value + "=" * (-len(value) % 4), altchars=b"-_", validate=True)
    except (ValueError, binascii.Error):
        raise ProtocolError() from None
    if len(decoded) != expected_length or base64(decoded) != value: raise ProtocolError()
    return decoded
