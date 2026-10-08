"""P256-only keys and deterministic ECDSA via cryptography, never custom EC."""
import json

from cryptography.exceptions import InvalidSignature, UnsupportedAlgorithm
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, utils

from .encoding import base64, fields, unbase64, MAX_REQUEST
from .errors import ProtocolError
from .primitives import sha256

_ORDER = int("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", 16)
# PKCS8 v0 and AlgorithmIdentifier(id-ecPublicKey, named secp256r1).
_PKCS8_PREFIX = bytes.fromhex("020100301306072a8648ce3d020106082a8648ce3d030107")


def _private(der):
    if type(der) is not bytes or len(der) < 2 or len(der) > MAX_REQUEST or der[0] != 0x30:
        raise ProtocolError()
    # Frame only the outer DER sequence. Key/curve/scalar decoding is native.
    length = der[1]
    offset = 2
    if length & 0x80:
        count = length & 0x7f
        if not 1 <= count <= 4 or len(der) < 2 + count or der[2] == 0:
            raise ProtocolError()
        length = int.from_bytes(der[2:2 + count], "big")
        offset += count
        if length < 128:
            raise ProtocolError()
    if offset + length != len(der) or not der[offset:].startswith(_PKCS8_PREFIX):
        raise ProtocolError()
    try:
        key = serialization.load_der_private_key(der, password=None)
        if not isinstance(key, ec.EllipticCurvePrivateKey) or not isinstance(key.curve, ec.SECP256R1):
            raise ProtocolError()
        return key
    except UnsupportedAlgorithm:
        raise ProtocolError("unsupported_dependency") from None
    except Exception:
        raise ProtocolError() from None


def generate() -> bytes:
    try:
        return ec.generate_private_key(ec.SECP256R1()).private_bytes(
            serialization.Encoding.DER, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())
    except UnsupportedAlgorithm:
        raise ProtocolError("unsupported_dependency") from None
    except Exception:
        raise ProtocolError() from None


def public_jwk(private_der: bytes) -> dict:
    point = _private(private_der).public_key().public_numbers()
    return {"crv": "P-256", "kty": "EC", "x": base64(point.x.to_bytes(32, "big")), "y": base64(point.y.to_bytes(32, "big"))}


def _public(jwk):
    value = fields(jwk, {"crv", "kty", "x", "y"})
    if value["crv"] != "P-256" or value["kty"] != "EC":
        raise ProtocolError()
    encoded = b"\x04" + unbase64(value["x"], 32) + unbase64(value["y"], 32)
    try:
        key = ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), encoded)
        return key
    except UnsupportedAlgorithm:
        raise ProtocolError("unsupported_dependency") from None
    except Exception:
        raise ProtocolError() from None


def validate_public(jwk: dict) -> dict:
    _public(jwk)
    return dict(jwk)


def thumbprint(jwk: dict) -> str:
    value = validate_public(jwk)
    canonical = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("ascii")
    return base64(sha256(canonical))


def sign(private_der: bytes, message: bytes) -> bytes:
    if type(message) is not bytes:
        raise ProtocolError()
    key = _private(private_der)
    try:
        der = key.sign(message, ec.ECDSA(hashes.SHA256(), deterministic_signing=True))
        r, s = utils.decode_dss_signature(der)
        return r.to_bytes(32, "big") + s.to_bytes(32, "big")
    except UnsupportedAlgorithm:
        raise ProtocolError("unsupported_dependency") from None
    except Exception:
        raise ProtocolError() from None


def verify(jwk: dict, message: bytes, signature: bytes) -> bool:
    key = _public(jwk)
    if type(message) is not bytes:
        raise ProtocolError()
    if type(signature) is not bytes or len(signature) != 64:
        return False
    r, s = int.from_bytes(signature[:32], "big"), int.from_bytes(signature[32:], "big")
    if not 1 <= r < _ORDER or not 1 <= s < _ORDER:
        return False
    try:
        key.verify(utils.encode_dss_signature(r, s), message, ec.ECDSA(hashes.SHA256()))
        return True
    except InvalidSignature:
        return False
    except UnsupportedAlgorithm:
        raise ProtocolError("unsupported_dependency") from None
    except Exception:
        raise ProtocolError() from None
