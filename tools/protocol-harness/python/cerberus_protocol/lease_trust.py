"""Separate lease signing-key authority; retained pins cannot revoke cached leases."""
import threading

from .encoding import text, integer, context
from .errors import ProtocolError
from .keys import thumbprint, verify
from .client_trust import _key


class LeaseTrust:
    def __init__(self, issuer, pinned_jwk, revision):
        self.issuer = text(issuer, True)
        if not self.issuer: raise ProtocolError()
        key = _key(pinned_jwk)
        self._current, self._revision = thumbprint(key), integer(revision)
        self._keys = {self._current: key}
        self._lock = threading.RLock()

    def lookup(self, kid):
        with self._lock:
            text(kid, True)
            if kid not in self._keys: raise ProtocolError()
            return dict(self._keys[kid])

    def transition(self, new_jwk, revision, old_lease_signature):
        new_key = _key(new_jwk); revision = integer(revision); fingerprint = thumbprint(new_key)
        with self._lock:
            if revision <= self._revision or fingerprint in self._keys: raise ProtocolError()
            signed = context(["cerberus-lease-key-transition-v1", self.issuer, self._current, fingerprint, revision])
            if not verify(self._keys[self._current], signed, old_lease_signature): raise ProtocolError()
            self._keys[fingerprint] = new_key
            self._current, self._revision = fingerprint, revision
