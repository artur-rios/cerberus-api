import importlib
import unittest

from cerberus_protocol.errors import ProtocolError
from tests.test_lease import expected, NOW, ISSUER
from tests.fixtures import fixture


class OfflineClockTests(unittest.TestCase):
    def setUp(self):
        try:
            self.api = importlib.import_module("cerberus_protocol.offline_clock")
            self.lease = importlib.import_module("cerberus_protocol.lease")
            trust_api = importlib.import_module("cerberus_protocol.lease_trust")
        except ModuleNotFoundError: self.fail("OfflineClock/Lease feature missing")
        self.wall, self.monotonic = NOW, 100
        self.clock = self.api.OfflineClock(lambda: self.wall, lambda: self.monotonic)
        self.claims = self.lease.claims(expected(), NOW, True, None)
        self.trust = trust_api.LeaseTrust(ISSUER, fixture("lease")["publicJwk"], 1)

    def test_GivenCachedLease_WhenClockRestarts_ThenOnlineRenewalRequired(self):
        self.clock.renew(self.claims, True)
        self.assertEqual(NOW, self.clock.effective_now())
        self.wall += 10; self.monotonic += 10
        self.assertEqual(NOW+10, self.clock.effective_now())
        self.clock.restart()
        token = self.lease.sign(self.claims, fixture("lease")["privateDer"])
        self.lease.verify(token, expected(), self.trust, NOW+10)
        with self.assertRaises(ProtocolError): self.clock.effective_now()

    def test_GivenWallOrMonotonicRegression_WhenObserved_ThenRenewalRequired(self):
        for regressed in ["wall", "monotonic"]:
            self.wall, self.monotonic = NOW, 100
            clock = self.api.OfflineClock(lambda: self.wall, lambda: self.monotonic)
            clock.renew(self.claims, True)
            self.wall += 10; self.monotonic += 10; clock.effective_now()
            setattr(self, regressed, getattr(self, regressed)-1)
            with self.subTest(regressed=regressed), self.assertRaises(ProtocolError): clock.effective_now()
            self.wall += 100; self.monotonic += 100
            with self.assertRaises(ProtocolError): clock.effective_now()

    def test_GivenCachedImportOrUnauthenticatedRenewal_WhenUsed_ThenCannotResetAnchor(self):
        self.clock.renew(self.claims, True); self.wall += 10; self.monotonic += 10
        self.assertEqual(NOW+10, self.clock.effective_now())
        with self.assertRaises(ProtocolError): self.clock.renew(self.claims, False)
        self.assertEqual(NOW+10, self.clock.effective_now())

    def test_GivenServerTimeAndHighWater_WhenRenewed_ThenTimeNeverMovesBack(self):
        self.wall = NOW-100; self.clock.renew(self.claims, True)
        self.monotonic += 10; self.assertEqual(NOW+10, self.clock.effective_now())
        self.clock.restart()
        fresh = self.lease.claims(expected(), NOW+5, True, None)
        self.clock.renew(fresh, True)
        self.assertEqual(NOW+10, self.clock.effective_now())

    def test_GivenDisabledExpiry_WhenVerified_ThenNoPeriodicClockAnchorRequired(self):
        disabled = self.lease.claims(expected(False), NOW, False, None)
        token = self.lease.sign(disabled, fixture("lease")["privateDer"])
        self.assertEqual(disabled, self.lease.verify(token, expected(False), self.trust, NOW+10**9))
        with self.assertRaises(ProtocolError): self.clock.effective_now()

    def test_GivenInvalidTimeOrOverflow_WhenObserved_ThenRejected(self):
        self.clock.renew(self.claims, True); self.wall = True
        with self.assertRaises(ProtocolError): self.clock.effective_now()
        self.wall = NOW; self.clock.renew(self.claims, True); self.monotonic = 9007199254740991
        with self.assertRaises(ProtocolError): self.clock.effective_now()


if __name__ == "__main__": unittest.main()
