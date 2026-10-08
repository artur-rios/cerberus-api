"""Context-bound content encryption and reference-only consumed-salt accounting."""
from dataclasses import dataclass
import json
import secrets
import threading

from .encoding import context, guid, integer, text, fields, base64, unbase64, MAX_REQUEST
from .errors import ProtocolError
from .primitives import hkdf, gcm_seal, gcm_open, sha256

CONTENT = "cerberus-content-v1"
CONTENT_KINDS = frozenset(("account", "profile", "record", "folder", "collection"))
_FIELDS = {"format", "keyEpoch", "keySalt", "nonce", "ciphertext", "tag"}


def _freeze(value):
    return tuple(_freeze(item) for item in value) if type(value) is list else value


def _thaw(value):
    return [_thaw(item) for item in value] if type(value) is tuple else value


@dataclass(frozen=True, slots=True)
class Context:
    owner_id: str
    resource_kind: str
    resource_id: str
    key_epoch: int
    extra_context: object

    def __post_init__(self):
        guid(self.owner_id)
        text(self.resource_kind, True)
        guid(self.resource_id)
        integer(self.key_epoch)
        # Validate and snapshot nested arrays; caller mutations cannot change identity.
        encoded = context(self.extra_context)
        if len(encoded) > MAX_REQUEST:
            raise ProtocolError()
        object.__setattr__(self, "extra_context", _freeze(json.loads(encoded)))

    def _values(self):
        return [self.owner_id, self.resource_kind, self.resource_id, self.key_epoch, _thaw(self.extra_context)]


def _material(value, length):
    if type(value) is not bytes or len(value) != length:
        raise ProtocolError()
    return value


class NonceGuard:
    """Local reference model, not cross-device persistence or a security boundary."""
    def __init__(self, initial_count: int):
        if type(initial_count) is not int or not 0 <= initial_count <= 2**32:
            raise ProtocolError()
        self._initial_count = initial_count
        self._seeded = False
        self._counts = {}
        self._used = set()
        self._lock = threading.Lock()

    def reserve(self, root: bytes, expected: Context, salt: bytes) -> None:
        _material(root, 32)
        _material(salt, 32)
        if type(expected) is not Context:
            raise ProtocolError()
        fingerprint = sha256(root + context(expected._values()))
        with self._lock:
            if fingerprint not in self._counts:
                self._counts[fingerprint] = self._initial_count if not self._seeded else 0
                self._seeded = True
            pair = (fingerprint, salt)
            if self._counts[fingerprint] >= 2**32 or pair in self._used:
                raise ProtocolError()
            self._used.add(pair)
            self._counts[fingerprint] += 1


def _expected(expected, format):
    if (type(expected) is not Context or format != CONTENT
            or expected.resource_kind not in CONTENT_KINDS or expected.extra_context != ()):
        raise ProtocolError()


def _bounded(envelope):
    # Raw request limits remain the parser's responsibility. Bound even direct
    # dictionary inputs before decoding any variable-sized base64 buffer.
    for key in ("format", "keySalt", "nonce", "ciphertext", "tag"):
        value = envelope[key]
        if type(value) is not str or len(value) > MAX_REQUEST:
            raise ProtocolError()
    if len(json.dumps(envelope, separators=(",", ":"), ensure_ascii=True)) > MAX_REQUEST:
        raise ProtocolError()


def _key(root, expected, format, salt):
    info = context(["cerberus-aead-key-v1", format, *expected._values()])
    return hkdf(root, salt, info, 32)


def _aad(expected, format, encoded_salt):
    return context(["cerberus-aead-v1", format, *expected._values(), encoded_salt])


def seal(root: bytes, expected: Context, format: str, plaintext: bytes, guard: NonceGuard) -> dict:
    return seal_fixture(root, expected, format, plaintext, guard, secrets.token_bytes(32), secrets.token_bytes(12))


def seal_fixture(root: bytes, expected: Context, format: str, plaintext: bytes, guard: NonceGuard,
                 salt: bytes, nonce: bytes) -> dict:
    """Explicit public-test material only. Normal clients call seal, never this API."""
    _expected(expected, format)
    return _seal_bound(root, expected, format, plaintext, guard, salt, nonce)


def _bound_expected(expected, format):
    if type(expected) is not Context:
        raise ProtocolError()
    if format == CONTENT:
        _expected(expected, format)
    elif format == "cerberus-password-wrap-v1":
        if expected.resource_kind not in ("account-protection", "profile-protection") or len(expected.extra_context) != 5:
            raise ProtocolError()
    elif format == "cerberus-recovery-wrap-v1":
        if expected.resource_kind != "recovery" or len(expected.extra_context) != 2:
            raise ProtocolError()
    else:
        raise ProtocolError()


def _seal_bound(root, expected, format, plaintext, guard, salt, nonce):
    """Package-internal composition; public content APIs remain content-only."""
    _bound_expected(expected, format)
    _material(root, 32)
    _material(salt, 32)
    _material(nonce, 12)
    if type(guard) is not NonceGuard:
        raise ProtocolError()
    guard.reserve(root, expected, salt)
    # Reservation remains consumed on any subsequent failure or unsent result.
    if type(plaintext) is not bytes or not 1 <= len(plaintext) <= MAX_REQUEST:
        raise ProtocolError()
    encoded_salt = base64(salt)
    ciphertext = gcm_seal(_key(root, expected, format, salt), nonce, _aad(expected, format, encoded_salt), plaintext)
    envelope = {"format": format, "keyEpoch": expected.key_epoch, "keySalt": encoded_salt,
                "nonce": base64(nonce), "ciphertext": base64(ciphertext[:-16]), "tag": base64(ciphertext[-16:])}
    _bounded(envelope)
    return envelope


def open_envelope(root: bytes, expected: Context, format: str, envelope: dict) -> bytes:
    _expected(expected, format)
    _material(root, 32)
    return _open_bound(root, expected, format, envelope)


def _validate_bound(expected, format, envelope):
    _bound_expected(expected, format)
    value = fields(envelope, _FIELDS)
    if value["format"] != format or integer(value["keyEpoch"]) != expected.key_epoch:
        raise ProtocolError()
    _bounded(value)
    unbase64(value["keySalt"], 32)
    unbase64(value["nonce"], 12)
    unbase64(value["tag"], 16)
    encoded = value["ciphertext"]
    if not encoded:
        raise ProtocolError()
    unbase64(encoded, len(encoded) * 3 // 4)
    return value


def _open_bound(root, expected, format, envelope):
    _material(root, 32)
    value = _validate_bound(expected, format, envelope)
    salt, nonce, tag = unbase64(value["keySalt"], 32), unbase64(value["nonce"], 12), unbase64(value["tag"], 16)
    encoded = value["ciphertext"]
    ciphertext = unbase64(encoded, len(encoded) * 3 // 4)
    return gcm_open(_key(root, expected, format, salt), nonce, _aad(expected, format, value["keySalt"]), ciphertext + tag)
