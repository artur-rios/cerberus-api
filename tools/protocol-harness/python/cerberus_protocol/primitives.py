"""Maintained native primitives; protocol parameters have no fallback."""
from cryptography.exceptions import UnsupportedAlgorithm
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.argon2 import Argon2id
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

from .errors import ProtocolError


def _bytes(*values):
    if any(type(value) is not bytes for value in values):
        raise ProtocolError()


def hkdf(root: bytes, salt: bytes, info: bytes, length: int) -> bytes:
    _bytes(root, salt, info)
    if not root or type(length) is not int or not 1 <= length <= 8160:
        raise ProtocolError()
    try:
        return HKDF(algorithm=hashes.SHA256(), length=length, salt=salt, info=info).derive(root)
    except UnsupportedAlgorithm:
        raise ProtocolError("unsupported_dependency") from None
    except Exception:
        raise ProtocolError() from None


def argon2(password: bytes, salt: bytes) -> bytes:
    _bytes(password, salt)
    if len(salt) != 16:
        raise ProtocolError()
    try:
        return Argon2id(salt=salt, length=32, iterations=3, lanes=4, memory_cost=65536).derive(password)
    except UnsupportedAlgorithm:
        raise ProtocolError("unsupported_dependency") from None
    except Exception:
        raise ProtocolError() from None


def _gcm(key, nonce, aad, value):
    _bytes(key, nonce, aad, value)
    if len(key) != 32 or len(nonce) != 12:
        raise ProtocolError()


def gcm_seal(key: bytes, nonce: bytes, aad: bytes, plaintext: bytes) -> bytes:
    _gcm(key, nonce, aad, plaintext)
    try:
        return AESGCM(key).encrypt(nonce, plaintext, aad)
    except UnsupportedAlgorithm:
        raise ProtocolError("unsupported_dependency") from None
    except Exception:
        raise ProtocolError() from None


def gcm_open(key: bytes, nonce: bytes, aad: bytes, ciphertext_and_tag: bytes) -> bytes:
    _gcm(key, nonce, aad, ciphertext_and_tag)
    if len(ciphertext_and_tag) < 16:
        raise ProtocolError()
    try:
        # Native one-shot decrypt returns only after authenticating the complete tag.
        return AESGCM(key).decrypt(nonce, ciphertext_and_tag, aad)
    except UnsupportedAlgorithm:
        raise ProtocolError("unsupported_dependency") from None
    except Exception:
        raise ProtocolError() from None


def sha256(value: bytes) -> bytes:
    _bytes(value)
    digest = hashes.Hash(hashes.SHA256())
    digest.update(value)
    return digest.finalize()
