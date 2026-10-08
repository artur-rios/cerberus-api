"""Process-local enabled-expiry clock model. Renew input is a trusted online event."""
import threading

from .encoding import integer, MAX_INTEGER
from .errors import ProtocolError
from .lease import _claims


class OfflineClock:
    def __init__(self, wall, monotonic):
        if not callable(wall) or not callable(monotonic): raise ProtocolError()
        self._wall, self._monotonic = wall, monotonic
        self._anchored, self._highest = False, 0
        self._lock = threading.RLock()

    def renew(self, freshly_verified_lease, authenticated_online):
        if authenticated_online is not True: raise ProtocolError()
        value = _claims(freshly_verified_lease)
        if value["renewalEnabled"] is not True: raise ProtocolError()
        with self._lock:
            wall, monotonic = integer(self._wall(), 0), integer(self._monotonic(), 0)
            self._server, self._receipt = value["iat"], monotonic
            self._last_wall, self._last_monotonic = wall, monotonic
            self._highest = max(self._highest, wall, self._server)
            self._anchored = True

    def effective_now(self):
        with self._lock:
            if not self._anchored: raise ProtocolError()
            try:
                wall, monotonic = integer(self._wall(), 0), integer(self._monotonic(), 0)
                if wall < self._last_wall or monotonic < self._last_monotonic: raise ProtocolError()
                projected = integer(self._server + monotonic - self._receipt, 0)
                self._highest = max(self._highest, wall, projected)
                self._last_wall, self._last_monotonic = wall, monotonic
                return self._highest
            except ProtocolError:
                self._anchored = False
                raise

    def restart(self):
        with self._lock: self._anchored = False
