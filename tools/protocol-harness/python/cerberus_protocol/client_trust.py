"""Explicitly provisioned account/key pins; no directory TOFU."""
import threading

from .encoding import guid, integer, context
from .errors import ProtocolError
from .keys import validate_public, thumbprint, verify
from .protection import _snapshot


def _key(jwk):
    return validate_public(_snapshot(jwk, {"crv", "kty", "x", "y"}))


class ClientTrust:
    def __init__(self, account_id, recipient_jwk, author_jwk, directory_revision):
        self._account_id = guid(account_id)
        self._recipient = _key(recipient_jwk)
        self._author = _key(author_jwk)
        self._revision = integer(directory_revision)
        if thumbprint(self._recipient) == thumbprint(self._author):
            raise ProtocolError()
        self._lock = threading.RLock()

    def _pins(self, account_id):
        with self._lock:
            if guid(account_id) != self._account_id:
                raise ProtocolError()
            return dict(self._recipient), dict(self._author)

    def transition(self, account_id, role, new_jwk, revision, old_author_signature):
        guid(account_id)
        integer(revision)
        new_key = _key(new_jwk)
        if role not in ("recipient-kem", "envelope-author"):
            raise ProtocolError()
        with self._lock:
            if account_id != self._account_id or revision <= self._revision:
                raise ProtocolError()
            old = self._recipient if role == "recipient-kem" else self._author
            other = self._author if role == "recipient-kem" else self._recipient
            new_fingerprint = thumbprint(new_key)
            if new_fingerprint in (thumbprint(old), thumbprint(other)):
                raise ProtocolError()
            signed = context(["cerberus-key-transition-v1", account_id, role, thumbprint(old), new_fingerprint, revision])
            if not verify(self._author, signed, old_author_signature):
                raise ProtocolError()
            if role == "recipient-kem": self._recipient = new_key
            else: self._author = new_key
            self._revision = revision
